using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm : IAggregateRoot
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
    /// an observation at rest writes it, each through the moves of Vm.Creation.cs, Vm.Start.cs, Vm.Stop.cs,
    /// Vm.Deletion.cs and Vm.Observation.cs.
    public VmStatus Status { get; private set; }
    /// The Incus word the worker read last, word for word, null while none is known
    /// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim): one of the five statuses whose names
    /// are Incus words (IsIncusStatus). Not on /v1: billing reads it, and the reconciler compares the instance with it.
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
    public bool HasNetwork =>
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
    public static bool IsIncusStatus(VmStatus status) =>
        status == VmStatus.Running
        || status == VmStatus.Ready
        || status == VmStatus.Stopped
        || status == VmStatus.Frozen
        || status == VmStatus.Error;

    /// A worker reports only the five Incus words; any other status in a report is the worker's bug. No word at all
    /// (null) is a report that read none, not a foreign one.
    private static bool IsForeignStatus(VmStatus? word) => word != null && !IsIncusStatus(word.Value);

    /// An operation waits on its command: only the report carrying its id moves the status on. A snapshot create or
    /// revert waits on its own command, whose id the snapshot holds (titan-ops features/vm/design.md#vm-snapshot).
    private bool IsTransitionStatus =>
        Status == VmStatus.Creating
        || Status == VmStatus.Starting
        || Status == VmStatus.Stopping
        || Status == VmStatus.Deleting
        || Status == VmStatus.Snapshotting;

    private Vm() { }

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation): an operation from a person, refused when illegal.
    // What an operation may do in each Incus status is what Incus itself allows there
    // (titan-ops features/vm/design.md#operations-follow-incus). An intent only checks the move and writes the status,
    // under the operation id the handler chose; an accepted Stop, Start, Delete toward the provider or snapshot means
    // the VM has its ExternalId, which the handler then reads (titan-ops features/vm/design.md#vm-status).
    // A repeat or an already-done operation is refused; the handler recognises it before calling
    // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).

    /// An intent that changes the instance's status waits while an operation is running: only that operation's own
    /// report, or an observation at rest, moves the status on.
    private void EnsureNotInTransition()
    {
        if (IsTransitionStatus)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another operation for this VM is still in progress.");
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

    /// Intent, Titan-side display name only. The aggregate carries it out itself, so the check sits inside
    /// the mutation (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    public void Rename(string newName)
    {
        if (IsDeleted)
            throw new BusinessException("VM_INVALID_STATUS", "VM cannot be renamed in its current state.");
        Name = newName;
    }

    private static readonly System.Text.RegularExpressions.Regex NameShape = new(NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    /// The shape rule of a VM name. The command handlers ask it before they build or rename a VM
    /// (titan-ops architecture/domain-methods.md#names-and-sizes); the aggregate takes the name it is given.
    public static bool IsAName(string? name) =>
        name != null && name.Length > 0 && name.Length <= NameMaxLength && NameShape.IsMatch(name);

    /// The shape rule of the name of one of this VM's snapshots: 1 to 100 characters, not blank.
    public static bool IsASnapshotName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= VmSnapshot.NameMaxLength;

    /// The image's own size (0) or a positive number of GB.
    public static bool IsADiskSize(int diskSizeGb) => diskSizeGb >= ImageDiskSize;

    // The report of an operation's own command (titan-ops architecture/resource-lifecycle.md#shape) is the one thing that
    // ends an operation. One method per move, in the partial file of its operation (CreateSucceeded and CreateFailed in
    // Vm.Creation.cs, and so on): each checks the VM waits in that move's status under the reported id, and returns
    // false, changing nothing, for any other report (a Done that arrived twice, the report of another operation, a VM
    // already Deleted). The handler picks the method by the status and the outcome.
}
