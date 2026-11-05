using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

public class Vm : ProviderResource<VmOperationKind>, IAggregateRoot
{
    /// A VM's name: 1 to 64 letters, digits or hyphens, starting with a letter or digit (RFC 952 host name). The
    /// validators read these two, so the /v1 spec carries the same pattern (titan-ops features/vm/design.md#vm-name-limit).
    public const int NameMaxLength = 64;
    public const string NamePattern = "^[a-zA-Z0-9][a-zA-Z0-9-]*$";

    /// The disk size that asks for no override: the worker sizes the root disk only above it, so the VM gets its
    /// image's own size (OpenViCloud/titan-incus-worker:resource/vm/create.go).
    public const int ImageDiskSize = 0;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid NetworkId { get; private set; }
    public string Name { get; private set; } = default!;
    // Only an observation (Mark*), an operation's own report (FinishOperation, FailOperation) or a delete with nothing on the provider
    // (DeleteLocally) writes CurrentState and CurrentExistence; only
    // an operation, or a failed operation's report putting it back, writes DesiredState and DesiredExistence
    // (titan-ops architecture/desired-and-current.md#desired-and-current).
    // CurrentState is the Incus status word for word, null while Incus has no instance
    // (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    public VmState? CurrentState { get; private set; }
    public VmDesiredState DesiredState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    public string? IpAddress { get; private set; }
    public string? PublicIpv6Address { get; private set; }
    public string VmOfferingId { get; private set; } = default!;
    public string TemplateId { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public int DiskSizeGb { get; private set; }
    public string? UserData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// The VM's snapshots, each an instance snapshot on Incus named under the instance; rows are kept after Deleted
    /// (titan-ops features/vm/design.md#vm-snapshot). The repository loads them with the VM.
    private readonly List<VmSnapshot> _snapshots = [];
    public IReadOnlyCollection<VmSnapshot> Snapshots => _snapshots;

    /// What counts toward MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting).
    public int SnapshotQuotaUsage => _snapshots.Count(s => s.CountsTowardQuota);

    /// Counts toward MaxVms (titan-ops features/vm/design.md#counting-and-billing): the instance exists, or its create is
    /// still waiting. A create that failed before Incus had the instance, an instance not found, and a VM whose delete
    /// was asked do not count; a create that failed after Incus made the instance is Exists and counts.
    /// VmRepository.CountActiveByTenantAsync writes the same test in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && PendingOperationId != null));

    /// Billed while the instance holds CPU and RAM (titan-ops features/vm/design.md#counting-and-billing): every Incus
    /// status but Stopped keeps the qemu process alive (incus driver_qemu.go statusCode). A delete in flight still bills.
    /// VmRepository.ListBillableAsync writes the same test in SQL.
    public bool IsBilled =>
        CurrentExistence == CurrentExistence.Exists
        && (CurrentState == VmState.Running
            || CurrentState == VmState.Ready
            || CurrentState == VmState.Frozen
            || CurrentState == VmState.Error);

    /// Holds its network while the instance exists on Incus, whatever its status and a delete in flight included
    /// (Incus refuses to delete a network an instance still uses), or while its create is still waiting
    /// (titan-ops features/vm/design.md#counting-and-billing). Unlike OccupiesQuota, a delete that was asked for
    /// changes nothing here. NetworkRepository.HasActiveVmsAsync writes the same test in SQL.
    public bool HoldsItsNetwork =>
        CurrentExistence == CurrentExistence.Exists
        || (CurrentExistence == CurrentExistence.NotCreated && PendingOperationId != null);

    /// Deleted absorbs every later report and observation (titan-ops architecture/resource-lifecycle.md#invariants):
    /// ComputeMeterService bills a VM whose instance exists and runs, so a late poll that revived a deleted VM would
    /// charge for a machine that no longer exists. Until then the VM is still listed, still holds its name and is
    /// still observed; the repository's list, name and reconciler queries write !IsDeleted in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => CurrentExistence == CurrentExistence.Deleted;

    /// A snapshot command (create or revert) still touching the instance: the VM's delete waits for it.
    private bool ASnapshotCommandIsRunning => _snapshots.Any(s => s.BlocksVmDelete);

    /// The move the waiting operation makes, for display. Null with the desired fields differing from the current
    /// ones is drift.
    public VmTransition? Transition
    {
        get
        {
            switch (PendingOperationKind)
            {
                case VmOperationKind.Create:
                    return VmTransition.Creating;
                case VmOperationKind.Start:
                    return VmTransition.Starting;
                case VmOperationKind.Stop:
                    return VmTransition.Stopping;
                case VmOperationKind.Delete:
                    return VmTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private Vm() { }

    public static Vm Create(
        Guid tenantId,
        Guid networkId,
        string name,
        string vmOfferingId,
        string templateId,
        string clusterId,
        int diskSizeGb,
        string? userData = null)
    {
        RequireName(name);
        if (diskSizeGb < ImageDiskSize)
        {
            throw new BusinessException("INVALID_VM_DISK_SIZE",
                $"A VM disk is {ImageDiskSize} (the image's own size) or a positive number of GB.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var vm = new Vm
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NetworkId = networkId,
            Name = name,
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredState = VmDesiredState.Running,
            DesiredExistence = DesiredExistence.Exists,
            VmOfferingId = vmOfferingId,
            TemplateId = templateId,
            ClusterId = clusterId,
            DiskSizeGb = diskSizeGb,
            UserData = userData,
            CreatedAt = now,
        };
        // The create is an operation like the others: the create command's Done ends it.
        vm.SetPending(PendingOperation<VmOperationKind>.Of(Guid.NewGuid(), VmOperationKind.Create, now));
        return vm;
    }

    /// A delete was asked and still waits, or the VM is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => PendingOperationKind == VmOperationKind.Delete || IsDeleted;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation): an operation from a person, refused when illegal.
    // What an operation may do in each Incus status is what Incus itself allows there
    // (titan-ops features/vm/design.md#operations-follow-incus). The provider carries out stop/start/delete, so the
    // provider id leaves only on the ticket (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    // A repeat or an already-done operation is refused; the handler recognises it before calling
    // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).

    public OperationTicket Stop()
    {
        if (PendingOperationKind == VmOperationKind.Stop)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop is already in progress for this VM.");
        }
        if (PendingOperationKind != null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another operation for this VM is still in progress.");
        }
        if (CurrentState == VmState.Stopped)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already stopped.");
        }
        bool incusCanStopIt = CurrentState == VmState.Running
                           || CurrentState == VmState.Ready
                           || CurrentState == VmState.Frozen
                           || CurrentState == VmState.Error;
        if (!incusCanStopIt)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM can only be stopped while its instance is Running, Ready, Frozen or Error.");
        }
        return Accept(VmOperationKind.Stop, VmDesiredState.Stopped, DesiredExistence.Exists);
    }

    /// Start on a Frozen instance resumes it: Incus answers `start` from Frozen by thawing the guest.
    public OperationTicket Start()
    {
        if (PendingOperationKind == VmOperationKind.Start)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A start is already in progress for this VM.");
        }
        if (PendingOperationKind != null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another operation for this VM is still in progress.");
        }
        bool alreadyRunning = CurrentState == VmState.Running || CurrentState == VmState.Ready;
        if (alreadyRunning)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already running.");
        }
        bool incusCanStartIt = CurrentState == VmState.Stopped || CurrentState == VmState.Frozen;
        if (!incusCanStartIt)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM can only be started while its instance is Stopped or Frozen.");
        }
        return Accept(VmOperationKind.Start, VmDesiredState.Running, DesiredExistence.Exists);
    }

    /// Any VM with a provider id can be deleted: the worker force-stops whatever status Incus reports and deletes
    /// the instance, and an instance already gone counts as deleted. Without an id there is nothing on the provider
    /// unless the create is still waiting (it may yet instantiate), so there is no ticket and VmDeletion calls
    /// DeleteLocally(). Internal: VmDeletion is the only way in (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    internal OperationTicket? Delete()
    {
        if (IsDeleted)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        }
        if (PendingOperationKind == VmOperationKind.Delete)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A delete is already in progress for this VM.");
        }
        // A snapshot create or revert in flight would act on an instance being deleted
        // (titan-ops features/vm/design.md#vm-snapshot-counting): refused until it settles.
        if (ASnapshotCommandIsRunning)
        {
            throw new BusinessException("VM_SNAPSHOT_IN_PROGRESS", "A snapshot operation is in progress for this VM.");
        }

        bool reachedTheProvider = ExternalId != null;
        if (!reachedTheProvider)
        {
            EnsureNoCreateWaiting();
            return null;
        }

        return Accept(VmOperationKind.Delete, DesiredState, DesiredExistence.Deleted); // replaces a waiting stop/start (titan-ops features/vm/design.md#conflict-and-delete-wins)
    }

    private OperationTicket Accept(VmOperationKind kind, VmDesiredState desiredState, DesiredExistence desiredExistence)
    {
        string providerId = GetExternalId();
        PendingOperation<VmOperationKind> operation = PendingOperation<VmOperationKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);
        DesiredState = desiredState;
        DesiredExistence = desiredExistence;
        SetPending(operation);
        return OperationTicket.Of(operation.Id, providerId);
    }

    /// A create still waiting without a provider id may yet instantiate on the provider, so there is nothing safe
    /// to forget.
    private void EnsureNoCreateWaiting()
    {
        if (PendingOperationKind == VmOperationKind.Create)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is still being created; it can be deleted once the provider has it.");
        }
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    private string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM has no provider resource yet.");
        }
        return ExternalId;
    }

    /// The VM half of Volume.Attach. It runs BEFORE the cluster check there, so a VM in the wrong state
    /// answers VM_INVALID_STATUS even when the clusters also differ. An allowlist: a new VmState is refused.
    public string ProviderIdForVolumeAttach()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to attach a volume.");
        EnsureNoStopOrDeleteWaiting();
        return GetExternalId();
    }

    /// A load balancer of this VM's network may send to it: the instance is on Incus and its delete was not pressed.
    /// Any Incus state will do — the health check takes a stopped VM out of rotation
    /// (titan-ops features/load-balancer/design.md#lb-and-vm-network). Hands out the instance name the worker reads the
    /// MAC from (titan-ops features/load-balancer/design.md#backend-address).
    public string ProviderIdForLoadBalancerBackend(Guid networkId)
    {
        bool usable = NetworkId == networkId
                      && DesiredExistence == DesiredExistence.Exists
                      && CurrentExistence == CurrentExistence.Exists
                      && ExternalId != null;
        if (!usable)
            throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID",
                $"VM '{Id}' must be on Incus, on the load balancer's network, and not being deleted.");
        return ExternalId!;
    }

    /// Incus attaches a volume to, and snapshots, an instance that is Running, Ready, Stopped or Frozen
    /// (titan-ops features/vm/design.md#operations-follow-incus); Error is not offered.
    private bool IncusCanAttachOrSnapshot()
    {
        bool instanceExists = CurrentExistence == CurrentExistence.Exists;
        bool statusAllowsIt = CurrentState == VmState.Running
                           || CurrentState == VmState.Ready
                           || CurrentState == VmState.Stopped
                           || CurrentState == VmState.Frozen;
        return instanceExists && statusAllowsIt;
    }

    /// Another aggregate's command against this VM would race a waiting stop or delete on the provider,
    /// and CurrentState still reads Running until the worker reports the outcome
    /// (titan-ops features/vm/design.md#conflict-and-delete-wins).
    private void EnsureNoStopOrDeleteWaiting()
    {
        bool stopOrDeleteWaiting = PendingOperationKind == VmOperationKind.Stop || PendingOperationKind == VmOperationKind.Delete;
        if (stopOrDeleteWaiting)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop or delete is in progress for this VM.");
        }
    }

    /// A shell session execs inside the guest, which needs its agent, which runs only while Incus reports Running
    /// or Ready (titan-ops features/vm/design.md#ssh-session). A waiting stop or delete would cut the session.
    public string ShellInstanceName()
    {
        bool instanceExists = CurrentExistence == CurrentExistence.Exists;
        bool guestRuns = CurrentState == VmState.Running || CurrentState == VmState.Ready;
        bool canHoldAShell = instanceExists && guestRuns;
        if (!canHoldAShell)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running or Ready to open an SSH session.");
        }
        EnsureNoStopOrDeleteWaiting();
        return GetExternalId();
    }

    /// The precondition of AddSnapshot and RevertSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to snapshot or revert.");
        EnsureNoStopOrDeleteWaiting();
    }

    // ---- Snapshots: the VM counts its snapshots, keeps the "no delete under a snapshot command" rule, and routes each
    // call to the snapshot it names (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmSnapshot;
    // this is the way in.

    /// Intent: a new snapshot of this VM, with the id the handler chose. The VM must accept a snapshot
    /// (EnsureSnapshottable). The create operation is the snapshot's own Pending; its ticket carries the VM's provider
    /// id, the instance the command snapshots.
    public OperationTicket AddSnapshot(Guid snapshotId, string name)
    {
        EnsureSnapshottable();
        VmSnapshot snapshot = VmSnapshot.Create(snapshotId, Id, TenantId, name);
        _snapshots.Add(snapshot);
        return snapshot.TicketForCreate(GetExternalId());
    }

    /// A snapshot this VM does not have answers like a missing one, the same 404 a snapshot of another VM gets
    /// (titan-ops features/vm/design.md#vm-routes).
    private VmSnapshot GetSnapshot(Guid snapshotId)
    {
        foreach (VmSnapshot snapshot in _snapshots)
        {
            if (snapshot.Id == snapshotId)
            {
                return snapshot;
            }
        }
        throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
    }

    /// Intent on one snapshot: the VM must accept a revert (its own rule first), then the snapshot must rest on
    /// Incus; returns the operation and the snapshot's provider id for the command. Titan does not stop or start the VM
    /// for it.
    public OperationTicket RevertSnapshot(Guid snapshotId)
    {
        VmSnapshot snapshot = GetSnapshot(snapshotId);
        EnsureSnapshottable();
        return snapshot.Revert();
    }

    /// Intent on one snapshot: its delete goes through Incus; returns the operation and the snapshot's provider id for
    /// the command, or no ticket when the snapshot never reached Incus and is deleted here.
    public OperationTicket? DeleteSnapshot(Guid snapshotId) => GetSnapshot(snapshotId).Delete();

    /// The report that the snapshot's own command ended Done (titan-ops architecture/resource-lifecycle.md#shape); null
    /// when it names another operation than the one the snapshot waits on.
    public VmSnapshotOperationKind? FinishSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId) =>
        GetSnapshot(snapshotId).FinishOperation(operationId, externalId);

    /// The report that the snapshot's own command gave up; null when it names another operation than the one the
    /// snapshot waits on.
    public VmSnapshotOperationKind? FailSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId,
        string? reason, string? detail) =>
        GetSnapshot(snapshotId).FailOperation(operationId, externalId, reason, detail);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkSnapshotObserved(Guid snapshotId) => GetSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => GetSnapshot(snapshotId).MarkNotFound();

    /// Intent, Titan-side display name only. The aggregate carries it out itself, so the check sits inside
    /// the mutation (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    public void Rename(string newName)
    {
        if (IsDeleted)
            throw new BusinessException("VM_INVALID_STATUS", "VM cannot be renamed in its current state.");
        RequireName(newName);
        Name = newName;
    }

    private static readonly System.Text.RegularExpressions.Regex NameShape = new(NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void RequireName(string? name)
    {
        if (name == null || name.Length == 0 || name.Length > NameMaxLength || !NameShape.IsMatch(name))
        {
            throw new BusinessException("INVALID_VM_NAME",
                $"A VM name is 1 to {NameMaxLength} letters, digits or hyphens, starting with a letter or digit.");
        }
    }

    /// Intent: a VM that never reached the provider has no command and no observation coming, so the
    /// aggregate writes the current fields itself (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void DeleteLocally()
    {
        if (IsDeleted)
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        bool reachedTheProvider = ExternalId != null;
        if (reachedTheProvider)
            throw new BusinessException("VM_INVALID_STATUS", "VM exists at the provider; only its delete command can end it.");
        EnsureNoCreateWaiting();
        CurrentState = null;
        CurrentExistence = CurrentExistence.Deleted;
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(null);
    }

    // ---- The report of an operation's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // an operation. It writes both axes — what the worker saw is current, and a failure puts desired back — so it is
    // neither an intent nor a Mark*.

    /// The command ended Done. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null: a Done that arrived twice, a report for a VM already Deleted. Records what the worker saw, gives
    /// the operation its effect and clears an older failure (titan-ops architecture/resource-lifecycle.md#kinds).
    /// A null status: the worker did not read the instance, so the addresses are not read either.
    public VmOperationKind? FinishOperation(Guid operationId, string? externalId,
        VmState? status, string? ipAddress, string? publicIpv6Address)
    {
        if (IsDeleted) return null;
        PendingOperation<VmOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RequireProviderIdOnDone(ExternalId, externalId);
        RecordWhatTheWorkerSaw(externalId, status, ipAddress, publicIpv6Address);

        ClearFailure();
        if (waiting.Kind == VmOperationKind.Delete)
        {
            // Incus deletes an instance's snapshots with it (titan-ops features/vm/design.md#vm-snapshot): every
            // snapshot not yet Deleted goes with the VM.
            CurrentExistence = CurrentExistence.Deleted;
            CurrentState = null;
            IpAddress = null;
            PublicIpv6Address = null;
            foreach (VmSnapshot snapshot in _snapshots)
            {
                snapshot.EndWithItsVm();
            }
        }

        SetPending(null);
        return waiting.Kind;
    }

    /// The command gave up. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null: a stale give-up, a report for a VM already Deleted. Records what the worker saw and the reason,
    /// and puts desired back to current according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
    /// A null status: the worker did not read the instance, so the addresses are not read either.
    public VmOperationKind? FailOperation(Guid operationId, string? externalId,
        VmState? status, string? ipAddress, string? publicIpv6Address, string? reason, string? detail)
    {
        if (IsDeleted) return null;
        PendingOperation<VmOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RecordWhatTheWorkerSaw(externalId, status, ipAddress, publicIpv6Address);

        RecordFailure(reason, detail);
        switch (waiting.Kind)
        {
            case VmOperationKind.Delete:
                DesiredExistence = DesiredExistence.Exists;
                break;
            case VmOperationKind.Start:
            case VmOperationKind.Stop:
                DesiredState = CurrentState == VmState.Stopped ? VmDesiredState.Stopped : VmDesiredState.Running;
                break;
            case VmOperationKind.Create:
                // A failed create may be deleted, not retried: desired stays, the reason says why it is not so.
                break;
        }

        SetPending(null);
        return waiting.Kind;
    }

    private PendingOperation<VmOperationKind>? WaitingOperationNamed(Guid operationId)
    {
        PendingOperation<VmOperationKind>? waiting = Pending;
        bool reportIsForThePendingOperation = waiting != null && waiting.Id == operationId;
        if (!reportIsForThePendingOperation)
        {
            return null;
        }
        return waiting;
    }

    // What the worker saw, whatever the outcome: a create that failed after Incus made the instance leaves a VM whose
    // delete still reaches the provider.
    private void RecordWhatTheWorkerSaw(string? externalId, VmState? status, string? ipAddress, string? publicIpv6Address)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (status != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = status;
            IpAddress = ipAddress;
            PublicIpv6Address = publicIpv6Address;
        }
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what the provider is,
    // so no transition guard and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants). Running seen
    // before a stop finishes is legitimate; throwing would fail the internal callback and leave the VM stuck.

    /// The reconciler saw the instance with this Incus status and these addresses.
    public void MarkObserved(VmState status, string? ipAddress, string? publicIpv6Address)
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = status;
        IpAddress = ipAddress;
        PublicIpv6Address = publicIpv6Address;
    }

    /// The reconciler did not find the instance. Never Deleted: pointing at the wrong cluster also finds nothing
    /// (titan-ops architecture/desired-and-current.md#reconciler-never-dies); only the delete command's Done ends a VM.
    public void MarkNotFound()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
        IpAddress = null;
        PublicIpv6Address = null;
    }
}
