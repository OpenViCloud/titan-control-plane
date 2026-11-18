using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Vm;

/// Adding a set of SSH keys to a running VM at once, through Incus's file API (titan-ops features/vm/design.md#ssh-key).
/// A child of Vm (titan-ops architecture/code-structure.md#child-entity): the VM's own status never changes for it, so
/// only the root reaches the methods below, and handlers go through Vm.AddKeys, KeyAdditionSucceeded,
/// KeyAdditionFailed and EndWithItsVm.
///
/// One operation, no counterpart on Incus to observe once it ends: unlike the eight types on
/// titan-ops architecture/resource-lifecycle.md#shape, this entity carries no Operation value object and no
/// MarkObserved/MarkNotFound — there is nothing a reconciler could see here that the one report does not already
/// settle. Its waiting operation and last failure are flat properties instead, named as the other types' Operation
/// is (PendingId, AskedAt, FailureReason, FailureDetail, FailedAt), without LastSeenStatus, which nothing sets.
public partial class VmKeyAddition : IEntity
{
    public Guid Id { get; private set; }
    public Guid VmId { get; private set; }
    public Guid TenantId { get; private set; }
    private readonly List<Guid> _sshKeyIds = [];
    /// The ids the caller picked, as validated at the moment this was asked (titan-ops features/vm/design.md#ssh-key):
    /// a key later removed from the tenant does not change this list.
    public IReadOnlyList<Guid> SshKeyIds => _sshKeyIds;
    /// The one status this entity shows (titan-ops features/vm/design.md#ssh-key). Written only by Create, Succeeded,
    /// Failed and EndWithItsVm.
    public VmKeyAdditionStatus Status { get; private set; }
    public Guid? PendingId { get; private set; }
    public DateTimeOffset? AskedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public string? FailureDetail { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    /// Whether the customer must restart the VM for sshd to pick up the new keys: true for a VM that lacked
    /// 10-openvicloud.conf before this operation wrote it too (titan-ops features/vm/design.md#ssh-key).
    public bool RestartNeeded { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private VmKeyAddition() { }

    /// Added or Failed: nothing more will ever happen here. The one terminal-absorb guard this entity needs
    /// (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs) — there is no Mark* method to check it in,
    /// since there is nothing to observe once the operation ends.
    public bool IsTerminal => Status != VmKeyAdditionStatus.Adding;

    /// The one report that could end this: the operation's own, by id, while it still waits.
    private bool IsWaitingOn(Guid operationId, VmKeyAdditionStatus waiting) =>
        Status == waiting && PendingId == operationId;

    /// Called by Vm.AddKeys after the VM checked itself, with the ids the handler chose: this entity's and its
    /// operation's.
    internal static VmKeyAddition Create(Guid id, Guid vmId, Guid tenantId, List<Guid> sshKeyIds, Guid operationId)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "A key addition needs an id.");
        }
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        var addition = new VmKeyAddition
        {
            Id = id,
            VmId = vmId,
            TenantId = tenantId,
            Status = VmKeyAdditionStatus.Adding,
            PendingId = operationId,
            AskedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        addition._sshKeyIds.AddRange(sshKeyIds);
        return addition;
    }

    /// The operation ended Done: the keys are on the VM. Ignored, returning false, when it names another operation
    /// than the one this entity waits on.
    internal bool Succeeded(Guid operationId, bool restartNeeded)
    {
        if (!IsWaitingOn(operationId, VmKeyAdditionStatus.Adding))
        {
            return false;
        }
        Status = VmKeyAdditionStatus.Added;
        RestartNeeded = restartNeeded;
        PendingId = null;
        AskedAt = null;
        return true;
    }

    /// The operation gave up: nothing was added, and the reason is recorded. Ignored, returning false, when it names
    /// another operation than the one this entity waits on.
    internal bool Failed(Guid operationId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VmKeyAdditionStatus.Adding))
        {
            return false;
        }
        Status = VmKeyAdditionStatus.Failed;
        FailureReason = reason;
        FailureDetail = detail;
        FailedAt = DateTimeOffset.UtcNow;
        PendingId = null;
        AskedAt = null;
        return true;
    }

    /// Incus deleted the instance, and this operation with it: nothing will ever report on it again
    /// (titan-ops architecture/resource-lifecycle.md#shape, "a root's delete ends every child still waiting"). An
    /// entity already Added or Failed stays as it is.
    internal void EndWithItsVm()
    {
        if (IsTerminal)
        {
            return;
        }
        Status = VmKeyAdditionStatus.Failed;
        FailureReason = "VM_DELETED";
        FailureDetail = "The VM was deleted before this operation reported.";
        FailedAt = DateTimeOffset.UtcNow;
        PendingId = null;
        AskedAt = null;
    }
}
