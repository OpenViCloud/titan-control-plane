using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

public class Vm : IAggregateRoot
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
    /// The one status the API shows (titan-ops features/vm/design.md#vm-status). Only an operation, a report ending it, or
    /// an observation at rest writes it, each through the table of moves below.
    public VmStatus Status { get; private set; }
    /// The Incus word the worker read last, word for word, null while none is known
    /// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim): one of the five statuses whose names
    /// are Incus words (IsAnIncusWord). Not on /v1: billing reads it, and the reconciler compares the instance with it.
    public VmStatus? ObservedState { get; private set; }
    /// The operation waiting on its command's report, or null. The aggregate only compares a report's id with it; the
    /// status says what the operation asks for (titan-ops architecture/resource-lifecycle.md#invariants).
    public Guid? PendingOperationId { get; private set; }
    public DateTimeOffset? PendingOperationAskedAt { get; private set; }
    // An operation that failed (titan-ops architecture/desired-and-current.md#operation-failure): a fixed reason and the
    // raw detail; the detail is admin-only. Cleared when a later operation ends Done.
    public string? OperationFailureReason { get; private set; }
    public string? OperationFailureDetail { get; private set; }
    public DateTimeOffset? OperationFailedAt { get; private set; }
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

    /// Counts toward MaxVms (titan-ops features/vm/design.md#counting-and-billing): the instance exists or its create
    /// waits. A create that failed before Incus had the instance, an instance not found, and a VM whose delete was asked
    /// do not count. VmRepository.CountActiveByTenantAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota =>
        Status != VmStatus.Deleting
        && Status != VmStatus.Deleted
        && Status != VmStatus.NotFound
        && Status != VmStatus.Failed;

    /// Billed while the instance holds CPU and RAM (titan-ops features/vm/design.md#counting-and-billing): every Incus
    /// status but Stopped keeps the qemu process alive (incus driver_qemu.go statusCode). A stop or delete in flight
    /// still bills while Incus last said so. Not found and Deleted need no test of their own: both clear the word, and
    /// a Deleted VM ignores every later observation. VmRepository.ListBillableAsync writes the same test in SQL.
    public bool IsBilled =>
        ObservedState == VmStatus.Running
        || ObservedState == VmStatus.Ready
        || ObservedState == VmStatus.Frozen
        || ObservedState == VmStatus.Error;

    /// Holds its network while the instance may be on Incus, a delete in flight included (Incus refuses to delete a
    /// network an instance still uses), or while its create waits (titan-ops features/vm/design.md#counting-and-billing).
    /// NetworkRepository.HasActiveVmsAsync writes the same test in SQL.
    public bool HoldsItsNetwork =>
        Status != VmStatus.Deleted
        && Status != VmStatus.NotFound
        && Status != VmStatus.Failed;

    /// Deleted absorbs every later report and observation (titan-ops architecture/resource-lifecycle.md#invariants):
    /// a late poll that revived a deleted VM would bill a machine that no longer exists. The repository's list, name and
    /// reconciler queries write !IsDeleted in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => Status == VmStatus.Deleted;

    /// Incus holds the instance as far as Titan last knew: it has a provider id, was not reported missing, and its
    /// delete has not ended. A backup has something to export only then.
    public bool HasAnInstance =>
        ExternalId != null
        && Status != VmStatus.NotFound
        && Status != VmStatus.Deleted;

    /// The five statuses that are Incus words for a QEMU instance (incus driver_qemu.go statusCode): the only ones a
    /// worker reports, and the only ones ObservedState holds.
    public static bool IsAnIncusWord(VmStatus status) =>
        status == VmStatus.Running
        || status == VmStatus.Ready
        || status == VmStatus.Stopped
        || status == VmStatus.Frozen
        || status == VmStatus.Error;

    /// An operation waits on its command: only the report carrying its id moves the status on.
    private bool IsInTransition =>
        Status == VmStatus.Creating
        || Status == VmStatus.Starting
        || Status == VmStatus.Stopping
        || Status == VmStatus.Deleting;

    /// A snapshot command (create or revert) still touching the instance: the VM's delete waits for it.
    private bool ASnapshotCommandIsRunning => _snapshots.Any(s => s.BlocksVmDelete);

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
            Status = VmStatus.Creating,
            ObservedState = null,
            VmOfferingId = vmOfferingId,
            TemplateId = templateId,
            ClusterId = clusterId,
            DiskSizeGb = diskSizeGb,
            UserData = userData,
            CreatedAt = now,
        };
        // The create is an operation like the others: the create command's Done ends it.
        vm.PendingOperationId = Guid.NewGuid();
        vm.PendingOperationAskedAt = now;
        return vm;
    }

    /// A delete was asked and still waits, or the VM is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == VmStatus.Deleting || IsDeleted;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation): an operation from a person, refused when illegal.
    // What an operation may do in each Incus status is what Incus itself allows there
    // (titan-ops features/vm/design.md#operations-follow-incus). An intent only checks the move and writes the status,
    // under the operation id the handler chose; an accepted Stop, Start, Delete toward the provider or snapshot means
    // the VM has its ExternalId, which the handler then reads (titan-ops features/vm/design.md#vm-status).
    // A repeat or an already-done operation is refused; the handler recognises it before calling
    // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).

    public void Stop(Guid operationId)
    {
        RequireOperationId(operationId);
        if (Status == VmStatus.Stopping)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop is already in progress for this VM.");
        }
        if (IsInTransition)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another operation for this VM is still in progress.");
        }
        if (Status == VmStatus.Stopped)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already stopped.");
        }
        bool incusCanStopIt = Status == VmStatus.Running
                           || Status == VmStatus.Ready
                           || Status == VmStatus.Frozen
                           || Status == VmStatus.Error;
        if (!incusCanStopIt)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM can only be stopped while its instance is Running, Ready, Frozen or Error.");
        }
        EnsureOnTheProvider();
        Status = VmStatus.Stopping;
        PendingOperationId = operationId;
        PendingOperationAskedAt = DateTimeOffset.UtcNow;
    }

    /// Start on a Frozen instance resumes it: Incus answers `start` from Frozen by thawing the guest.
    public void Start(Guid operationId)
    {
        RequireOperationId(operationId);
        if (Status == VmStatus.Starting)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A start is already in progress for this VM.");
        }
        if (IsInTransition)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another operation for this VM is still in progress.");
        }
        bool alreadyRunning = Status == VmStatus.Running || Status == VmStatus.Ready;
        if (alreadyRunning)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already running.");
        }
        bool incusCanStartIt = Status == VmStatus.Stopped || Status == VmStatus.Frozen;
        if (!incusCanStartIt)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM can only be started while its instance is Stopped or Frozen.");
        }
        EnsureOnTheProvider();
        Status = VmStatus.Starting;
        PendingOperationId = operationId;
        PendingOperationAskedAt = DateTimeOffset.UtcNow;
    }

    /// Any VM with a provider id can be deleted, and its status says how: Deleting, the worker force-stops whatever
    /// status Incus reports and deletes the instance (an instance already gone counts as deleted); or Deleted at once,
    /// for a VM that never reached the provider, which has no command and no observation coming
    /// (titan-ops architecture/desired-and-current.md#delete-locally). A create still waiting without a provider id may
    /// yet instantiate, so it is refused. Internal: VmDeletion is the only way in
    /// (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    internal void Delete(Guid operationId)
    {
        RequireOperationId(operationId);
        if (IsDeleted)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        }
        if (Status == VmStatus.Deleting)
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
            Status = VmStatus.Deleted;
            ObservedState = null;
            PendingOperationId = null;
            PendingOperationAskedAt = null;
            return;
        }

        // Replaces a waiting stop or start, whose report then no longer matches and is ignored
        // (titan-ops features/vm/design.md#conflict-and-delete-wins).
        EnsureOnTheProvider();
        Status = VmStatus.Deleting;
        PendingOperationId = operationId;
        PendingOperationAskedAt = DateTimeOffset.UtcNow;
    }

    /// An empty id would match no report, so the operation could never end; refused before anything changes.
    private static void RequireOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
    }

    /// A create still waiting without a provider id may yet instantiate on the provider, so there is nothing safe
    /// to forget.
    private void EnsureNoCreateWaiting()
    {
        if (Status == VmStatus.Creating)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is still being created; it can be deleted once the provider has it.");
        }
    }

    /// Only a VM the provider holds has a command to wait on, or an instance to name.
    private void EnsureOnTheProvider()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM has no provider resource yet.");
        }
    }

    /// The VM half of Volume.Attach. It runs BEFORE the cluster check there, so a VM in the wrong state
    /// answers VM_INVALID_STATUS even when the clusters also differ. An allowlist: a new status is refused.
    public void EnsureVolumeCanAttach()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to attach a volume.");
    }

    /// A load balancer of this VM's network may send to it: the instance is on Incus and its delete was not pressed.
    /// Any Incus state will do — the health check takes a stopped VM out of rotation
    /// (titan-ops features/load-balancer/design.md#lb-and-vm-network). The worker reads the MAC from the instance
    /// ExternalId names (titan-ops features/load-balancer/design.md#backend-address).
    public void EnsureLoadBalancerCanSendTo(Guid networkId)
    {
        bool instanceIsThere = Status == VmStatus.Running
                               || Status == VmStatus.Ready
                               || Status == VmStatus.Stopped
                               || Status == VmStatus.Frozen
                               || Status == VmStatus.Error
                               || Status == VmStatus.Starting
                               || Status == VmStatus.Stopping;
        bool usable = NetworkId == networkId && instanceIsThere && ExternalId != null;
        if (!usable)
            throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID",
                $"VM '{Id}' must be on Incus, on the load balancer's network, and not being deleted.");
    }

    /// Incus attaches a volume to, and snapshots, an instance that is Running, Ready, Stopped or Frozen
    /// (titan-ops features/vm/design.md#operations-follow-incus); Error is not offered. A waiting start still leaves the
    /// instance Stopped on Incus; a waiting stop or delete would race the command
    /// (titan-ops features/vm/design.md#conflict-and-delete-wins).
    private bool IncusCanAttachOrSnapshot() =>
        Status == VmStatus.Running
        || Status == VmStatus.Ready
        || Status == VmStatus.Stopped
        || Status == VmStatus.Frozen
        || Status == VmStatus.Starting;

    /// A shell session execs inside the guest, which needs its agent, which runs only while Incus reports Running
    /// or Ready (titan-ops features/vm/design.md#ssh-session). A waiting stop or delete would cut the session, and its
    /// status is then Stopping or Deleting.
    public void EnsureShellCanOpen()
    {
        bool guestRuns = Status == VmStatus.Running || Status == VmStatus.Ready;
        if (!guestRuns)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running or Ready to open an SSH session.");
        }
    }

    /// The precondition of AddSnapshot and RevertSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to snapshot or revert.");
    }

    // ---- Snapshots: the VM counts its snapshots, keeps the "no delete under a snapshot command" rule, and routes each
    // call to the snapshot it names (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmSnapshot;
    // this is the way in.

    /// Intent: a new snapshot of this VM, with the ids the handler chose: the snapshot's and its create's. The VM must
    /// accept a snapshot (EnsureSnapshottable). The create is the snapshot's own Pending; its command snapshots the
    /// instance ExternalId names.
    public void AddSnapshot(Guid snapshotId, Guid operationId, string name)
    {
        RequireOperationId(operationId);
        EnsureSnapshottable();
        EnsureOnTheProvider();
        VmSnapshot snapshot = VmSnapshot.Create(snapshotId, Id, TenantId, name, operationId);
        _snapshots.Add(snapshot);
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

    /// Intent on one snapshot, under the id the handler chose: the VM must accept a revert (its own rule first), then
    /// the snapshot must rest on Incus. Titan does not stop or start the VM for it.
    public void RevertSnapshot(Guid snapshotId, Guid operationId)
    {
        RequireOperationId(operationId);
        VmSnapshot snapshot = GetSnapshot(snapshotId);
        EnsureSnapshottable();
        snapshot.Revert(operationId);
    }

    /// Intent on one snapshot, under the id the handler chose: its delete goes through Incus, or, when the snapshot
    /// never reached Incus, it is Deleted here at once.
    public void DeleteSnapshot(Guid snapshotId, Guid operationId)
    {
        RequireOperationId(operationId);
        GetSnapshot(snapshotId).Delete(operationId);
    }

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

    // ---- The report of an operation's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends an operation. One method per move: each checks the VM waits in that move's status under the reported id, and
    // returns false, changing nothing, for any other report (a Done that arrived twice, the report of an operation a
    // delete replaced, a VM already Deleted). The handler picks the method by the status and the outcome.

    /// The create command's Done: Incus holds the instance under externalId. Without a word the worker did not read
    /// it, so the status is Unknown until the next observation. A Done must name the instance, or nothing would be
    /// left to delete it through; a worker that sends none has a bug, and does not retry a 422.
    public bool CreateSucceeded(Guid operationId, string? externalId, VmStatus? word, string? ipAddress,
        string? publicIpv6Address)
    {
        if (Status != VmStatus.Creating || PendingOperationId != operationId)
        {
            return false;
        }
        if (string.IsNullOrEmpty(externalId))
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
        RequireIncusWord(word);
        ExternalId = externalId;
        ObservedState = word;
        IpAddress = ipAddress;
        PublicIpv6Address = publicIpv6Address;
        OperationFailureReason = null;
        OperationFailureDetail = null;
        OperationFailedAt = null;
        if (word == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            Status = word.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The create command gave up. Before Incus held an instance the VM is Failed; after (the report names it) it
    /// rests in the Incus word last seen, Unknown when none was.
    public bool CreateFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (Status != VmStatus.Creating || PendingOperationId != operationId)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(externalId))
        {
            ExternalId = externalId;
        }
        OperationFailureReason = reason;
        OperationFailureDetail = detail;
        OperationFailedAt = DateTimeOffset.UtcNow;
        if (ExternalId == null)
        {
            Status = VmStatus.Failed;
        }
        else if (ObservedState == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            Status = ObservedState.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The start command's Done: the status is the word Incus reports. Without a word the old one may be stale, so it is
    /// forgotten and the status is Unknown: the reconciler reports the instance next cycle.
    public bool StartSucceeded(Guid operationId, VmStatus? word, string? ipAddress, string? publicIpv6Address)
    {
        if (Status != VmStatus.Starting || PendingOperationId != operationId)
        {
            return false;
        }
        RequireIncusWord(word);
        OperationFailureReason = null;
        OperationFailureDetail = null;
        OperationFailedAt = null;
        if (word == null)
        {
            ObservedState = null;
            Status = VmStatus.Unknown;
        }
        else
        {
            ObservedState = word;
            IpAddress = ipAddress;
            PublicIpv6Address = publicIpv6Address;
            Status = word.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The start command gave up: back to the Incus word last seen, with the reason
    /// (titan-ops architecture/desired-and-current.md#abandoned-operation).
    public bool StartFailed(Guid operationId, string? reason, string? detail)
    {
        if (Status != VmStatus.Starting || PendingOperationId != operationId)
        {
            return false;
        }
        OperationFailureReason = reason;
        OperationFailureDetail = detail;
        OperationFailedAt = DateTimeOffset.UtcNow;
        if (ObservedState == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            Status = ObservedState.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The stop command's Done: the status is the word Incus reports; without a word, Unknown, as StartSucceeded.
    public bool StopSucceeded(Guid operationId, VmStatus? word, string? ipAddress, string? publicIpv6Address)
    {
        if (Status != VmStatus.Stopping || PendingOperationId != operationId)
        {
            return false;
        }
        RequireIncusWord(word);
        OperationFailureReason = null;
        OperationFailureDetail = null;
        OperationFailedAt = null;
        if (word == null)
        {
            ObservedState = null;
            Status = VmStatus.Unknown;
        }
        else
        {
            ObservedState = word;
            IpAddress = ipAddress;
            PublicIpv6Address = publicIpv6Address;
            Status = word.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The stop command gave up: back to the Incus word last seen, with the reason.
    public bool StopFailed(Guid operationId, string? reason, string? detail)
    {
        if (Status != VmStatus.Stopping || PendingOperationId != operationId)
        {
            return false;
        }
        OperationFailureReason = reason;
        OperationFailureDetail = detail;
        OperationFailedAt = DateTimeOffset.UtcNow;
        if (ObservedState == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            Status = ObservedState.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The delete command's Done: the VM is Deleted, and Incus deleted its snapshots with it
    /// (titan-ops features/vm/design.md#vm-snapshot), so every snapshot not yet Deleted goes with it.
    public bool DeleteSucceeded(Guid operationId)
    {
        if (Status != VmStatus.Deleting || PendingOperationId != operationId)
        {
            return false;
        }
        OperationFailureReason = null;
        OperationFailureDetail = null;
        OperationFailedAt = null;
        Status = VmStatus.Deleted;
        ObservedState = null;
        IpAddress = null;
        PublicIpv6Address = null;
        foreach (VmSnapshot snapshot in _snapshots)
        {
            snapshot.EndWithItsVm();
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// The delete command gave up: back to the Incus word last seen, with the reason; the next delete is a new
    /// operation (titan-ops architecture/desired-and-current.md#abandoned-operation).
    public bool DeleteFailed(Guid operationId, string? reason, string? detail)
    {
        if (Status != VmStatus.Deleting || PendingOperationId != operationId)
        {
            return false;
        }
        OperationFailureReason = reason;
        OperationFailureDetail = detail;
        OperationFailedAt = DateTimeOffset.UtcNow;
        if (ObservedState == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            Status = ObservedState.Value;
        }
        PendingOperationId = null;
        PendingOperationAskedAt = null;
        return true;
    }

    /// A worker reports only the five Incus words; any other status in a report is the worker's bug.
    private static void RequireIncusWord(VmStatus? word)
    {
        if (word != null && !IsAnIncusWord(word.Value))
        {
            throw new BusinessException("INVALID_STATUS", $"'{word}' is not an Incus status.");
        }
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what the
    // provider is, so no guard and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants). While
    // an operation waits the status stays; only the word is recorded. Running seen before a stop finishes is legitimate.

    /// The reconciler saw the instance with this Incus word and these addresses.
    public void MarkObserved(VmStatus word, string? ipAddress, string? publicIpv6Address)
    {
        RequireIncusWord(word);
        if (IsDeleted || Status == VmStatus.Failed) return;
        ObservedState = word;
        IpAddress = ipAddress;
        PublicIpv6Address = publicIpv6Address;
        if (!IsInTransition)
        {
            Status = word;
        }
    }

    /// The reconciler did not find the instance. Never Deleted: pointing at the wrong cluster also finds nothing
    /// (titan-ops architecture/desired-and-current.md#reconciler-never-dies); only the delete command's Done ends a VM.
    public void MarkNotFound()
    {
        if (IsDeleted || Status == VmStatus.Failed) return;
        ObservedState = null;
        IpAddress = null;
        PublicIpv6Address = null;
        if (!IsInTransition)
        {
            Status = VmStatus.NotFound;
        }
    }
}
