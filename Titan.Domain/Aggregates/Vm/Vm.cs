using System.Linq.Expressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

public class Vm : RequestingRoot<VmRequestKind>
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
    // Only an observation (Mark*), a request's own report (EndRequest) or a delete with nothing on the provider
    // (DeleteLocally) writes CurrentState and CurrentExistence; only
    // a request, or a failed request's report putting it back, writes DesiredState and DesiredExistence
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
    /// was asked do not count; a create that failed after Incus made the instance is Exists and counts. Written once
    /// here; the repository counts with it in SQL.
    public static Expression<Func<Vm, bool>> OccupiesQuota { get; } = v =>
        v.DesiredExistence == DesiredExistence.Exists
        && (v.CurrentExistence == CurrentExistence.Exists
            || (v.CurrentExistence == CurrentExistence.NotCreated && v.PendingRequestId != null));

    /// Billed while the instance holds CPU and RAM (titan-ops features/vm/design.md#counting-and-billing): every Incus
    /// status but Stopped keeps the qemu process alive (incus driver_qemu.go statusCode). A delete in flight still bills.
    public static Expression<Func<Vm, bool>> IsBilled { get; } = v =>
        v.CurrentExistence == CurrentExistence.Exists
        && (v.CurrentState == VmState.Running
            || v.CurrentState == VmState.Ready
            || v.CurrentState == VmState.Frozen
            || v.CurrentState == VmState.Error);

    /// A snapshot command (create or revert) still touching the instance: the VM's delete waits for it.
    private bool ASnapshotCommandIsRunning => _snapshots.Any(s => s.BlocksVmDelete);

    /// The move the waiting request makes, for display. Null with the desired fields differing from the current
    /// ones is drift.
    public VmTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case VmRequestKind.Create:
                    return VmTransition.Creating;
                case VmRequestKind.Start:
                    return VmTransition.Starting;
                case VmRequestKind.Stop:
                    return VmTransition.Stopping;
                case VmRequestKind.Delete:
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
        // The create is a request like the others: the create command's Done ends it.
        vm.SetPending(PendingRequest<VmRequestKind>.Of(Guid.NewGuid(), VmRequestKind.Create, now));
        return vm;
    }

    /// Deleted absorbs every later report and observation (titan-ops architecture/resource-lifecycle.md#invariants).
    /// ComputeMeterService bills a VM whose instance exists and runs, so a late poll that revived a deleted VM
    /// would charge for a machine that no longer exists.
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// A delete was asked and still waits, or the VM is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => PendingRequestKind == VmRequestKind.Delete || IsTerminal;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation): a request from a person, refused when illegal.
    // What a request may do in each Incus status is what Incus itself allows there
    // (titan-ops features/vm/design.md#requests-follow-incus). The provider carries out stop/start/delete, so the
    // provider id leaves only on the ticket (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    // A repeat or an already-done request is refused; the handler recognises it before calling
    // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).

    public RequestTicket RequestStop()
    {
        if (PendingRequestKind == VmRequestKind.Stop)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop is already in progress for this VM.");
        }
        if (PendingRequestKind != null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another request for this VM is still in progress.");
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
        return Accept(VmRequestKind.Stop, VmDesiredState.Stopped, DesiredExistence.Exists);
    }

    /// Start on a Frozen instance resumes it: Incus answers `start` from Frozen by thawing the guest.
    public RequestTicket RequestStart()
    {
        if (PendingRequestKind == VmRequestKind.Start)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A start is already in progress for this VM.");
        }
        if (PendingRequestKind != null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another request for this VM is still in progress.");
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
        return Accept(VmRequestKind.Start, VmDesiredState.Running, DesiredExistence.Exists);
    }

    /// Any VM with a provider id can be deleted: the worker force-stops whatever status Incus reports and deletes
    /// the instance, and an instance already gone counts as deleted. Without an id there is nothing on the provider
    /// unless the create is still waiting (it may yet instantiate), so there is no ticket and VmDeletion calls
    /// DeleteLocally(). Internal: VmDeletion is the only way in (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    internal RequestTicket? RequestDelete()
    {
        if (IsTerminal)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        }
        if (PendingRequestKind == VmRequestKind.Delete)
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

        return Accept(VmRequestKind.Delete, DesiredState, DesiredExistence.Deleted); // replaces a waiting stop/start (titan-ops features/vm/design.md#conflict-and-delete-wins)
    }

    private RequestTicket Accept(VmRequestKind kind, VmDesiredState desiredState, DesiredExistence desiredExistence)
    {
        string providerId = RequireExternalId();
        PendingRequest<VmRequestKind> request = PendingRequest<VmRequestKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);
        DesiredState = desiredState;
        DesiredExistence = desiredExistence;
        SetPending(request);
        return RequestTicket.Of(request.Id, providerId);
    }

    /// A create still waiting without a provider id may yet instantiate on the provider, so there is nothing safe
    /// to forget.
    private void EnsureNoCreateWaiting()
    {
        if (PendingRequestKind == VmRequestKind.Create)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is still being created; it can be deleted once the provider has it.");
        }
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    private string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VM_INVALID_STATUS", "VM has no provider resource yet.");

    /// The VM half of Volume.Attach. It runs BEFORE the cluster check there, so a VM in the wrong state
    /// answers VM_INVALID_STATUS even when the clusters also differ. An allowlist: a new VmState is refused.
    public string ProviderIdForVolumeAttach()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to attach a volume.");
        EnsureNoStopOrDeleteWaiting();
        return RequireExternalId();
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
    /// (titan-ops features/vm/design.md#requests-follow-incus); Error is not offered.
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
        bool stopOrDeleteWaiting = PendingRequestKind == VmRequestKind.Stop || PendingRequestKind == VmRequestKind.Delete;
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
        return RequireExternalId();
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

    /// Intent: a new snapshot of this VM. The VM must accept a snapshot (EnsureSnapshottable). The create request is the
    /// snapshot's own Pending.
    public VmSnapshotTicket AddSnapshot(string name)
    {
        EnsureSnapshottable();
        VmSnapshot snapshot = VmSnapshot.Create(Id, TenantId, name);
        _snapshots.Add(snapshot);
        return new VmSnapshotTicket(snapshot, RequireExternalId());
    }

    /// A snapshot this VM does not have answers like a missing one, the same 404 a snapshot of another VM gets
    /// (titan-ops features/vm/design.md#vm-routes).
    private VmSnapshot RequireSnapshot(Guid snapshotId)
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
    /// Incus; returns the request and the snapshot's provider id for the command. Titan does not stop or start the VM
    /// for it.
    public RequestTicket RevertSnapshot(Guid snapshotId)
    {
        VmSnapshot snapshot = RequireSnapshot(snapshotId);
        EnsureSnapshottable();
        return snapshot.Revert();
    }

    /// Intent on one snapshot: its delete goes through Incus; returns the request and the snapshot's provider id for
    /// the command, or no ticket when the snapshot never reached Incus and is deleted here.
    public RequestTicket? RequestSnapshotDelete(Guid snapshotId) => RequireSnapshot(snapshotId).RequestDelete();

    /// The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape); null when it names
    /// another request than the one the snapshot waits on.
    public PendingRequest<VmSnapshotRequestKind>? EndSnapshotRequest(Guid snapshotId, Guid requestId, VmSnapshotRequestEnd end) =>
        RequireSnapshot(snapshotId).EndRequest(requestId, end);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkSnapshotObserved(Guid snapshotId) => RequireSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => RequireSnapshot(snapshotId).MarkNotFound();

    /// Intent, Titan-side display name only. The aggregate carries it out itself, so the check sits inside
    /// the mutation (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    public void Rename(string newName)
    {
        if (IsTerminal)
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
        if (IsTerminal)
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

    // ---- The report of a request's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // a request. It writes both axes — what the worker saw is current, and a failure puts desired back — so it is
    // neither an intent nor a Mark*.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null: a stale
    /// give-up, a Done that arrived twice, a report for a VM already Deleted. Done and Failed both record what the
    /// worker saw. Done gives the request its effect and clears an older failure; Failed records the reason and puts
    /// desired back to current according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
    public VmRequestEnded? EndRequest(Guid requestId, VmRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<VmRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }
        RequireProviderIdOnDone(end.Outcome, ExternalId, end.ExternalId);

        // What the worker saw, whatever the outcome: a create that failed after Incus made the instance leaves a VM
        // whose delete still reaches the provider.
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (end.Observed != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = end.Observed.Status;
            IpAddress = end.Observed.IpAddress;
            PublicIpv6Address = end.Observed.PublicIpv6Address;
        }

        var snapshotsWhoseDeleteEnded = new List<VmSnapshot>();
        if (end.Outcome == RequestOutcome.Done)
        {
            ClearFailure();
            if (waiting!.Kind == VmRequestKind.Delete)
            {
                // Incus deletes an instance's snapshots with it (titan-ops features/vm/design.md#vm-snapshot): every
                // snapshot not yet Deleted goes with the VM; the ones whose own delete was waiting are named for
                // their outcome.
                CurrentExistence = CurrentExistence.Deleted;
                CurrentState = null;
                IpAddress = null;
                PublicIpv6Address = null;
                foreach (VmSnapshot snapshot in _snapshots)
                {
                    bool itsOwnDeleteWasWaiting = snapshot.EndWithItsVm();
                    if (itsOwnDeleteWasWaiting)
                    {
                        snapshotsWhoseDeleteEnded.Add(snapshot);
                    }
                }
            }
        }
        else
        {
            RecordFailure(end.Reason, end.Detail);
            switch (waiting!.Kind)
            {
                case VmRequestKind.Delete:
                    DesiredExistence = DesiredExistence.Exists;
                    break;
                case VmRequestKind.Start:
                case VmRequestKind.Stop:
                    DesiredState = CurrentState == VmState.Stopped ? VmDesiredState.Stopped : VmDesiredState.Running;
                    break;
                case VmRequestKind.Create:
                    // A failed create may be deleted, not retried: desired stays, the reason says why it is not so.
                    break;
            }
        }

        SetPending(null);
        return new VmRequestEnded(waiting, snapshotsWhoseDeleteEnded);
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what the provider is,
    // so no transition guard and no request ends here (titan-ops architecture/resource-lifecycle.md#invariants). Running seen
    // before a stop finishes is legitimate; throwing would fail the internal callback and leave the VM stuck.

    /// The reconciler saw the instance with this Incus status and these addresses.
    public void MarkObserved(VmObservation observed)
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = observed.Status;
        IpAddress = observed.IpAddress;
        PublicIpv6Address = observed.PublicIpv6Address;
    }

    /// The reconciler did not find the instance. Never Deleted: pointing at the wrong cluster also finds nothing
    /// (titan-ops architecture/desired-and-current.md#reconciler-never-dies); only the delete command's Done ends a VM.
    public void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
        IpAddress = null;
        PublicIpv6Address = null;
    }
}
