using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    /// A delete was asked and still waits, or the VM is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == VmStatus.Deleting || IsDeleted;

    /// A VM is refused once it is deleted and while a delete is already running. With a provider id it is also refused
    /// while another operation runs, a snapshot create or revert included; otherwise it goes Deleting: the worker
    /// force-stops whatever status Incus reports and deletes the instance (an instance already gone counts as deleted).
    /// Without a provider id there is no command and no observation coming, so it is Deleted at once
    /// (titan-ops architecture/desired-and-current.md#delete-locally); a create still waiting without a provider id may
    /// yet instantiate, so it is refused. Internal: VmDeletion is the only way in
    /// (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    /// Hands back the instance name the delete command acts on, or null when the VM is Deleted here at once.
    internal string? Delete(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        if (IsDeleted)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        }
        if (Status == VmStatus.Deleting)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A delete is already in progress for this VM.");
        }
        bool reachedTheProvider = ExternalId != null;
        if (!reachedTheProvider)
        {
            EnsureNoCreateWaiting();
            Status = VmStatus.Deleted;
            Operation = Operation.ReturnedToRest();
            return null;
        }

        // A start, stop or snapshot still waiting finishes first: its report ends it, and only then is a delete asked
        // (titan-ops features/vm/design.md#conflict-and-delete-wins).
        EnsureNotInTransition();
        EnsureOnTheProvider();
        VmStatus? seen = InstanceStatus;
        Status = VmStatus.Deleting;
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, seen);
        return ExternalId;
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

    /// The delete command's Done: the VM is Deleted, and Incus deleted its snapshots with it
    /// (titan-ops features/vm/design.md#vm-snapshot), so every snapshot not yet Deleted goes with it.
    public bool DeleteSucceeded(Guid operationId)
    {
        if (Status != VmStatus.Deleting || Operation.PendingId != operationId)
        {
            return false;
        }
        Operation = Operation.Done();
        Status = VmStatus.Deleted;
        IpAddress = null;
        PublicIpv6Address = null;
        foreach (VmSnapshot snapshot in _snapshots)
        {
            snapshot.EndWithItsVm();
        }
        return true;
    }

    /// The delete command gave up: back to the Incus status the VM holds (Unknown without one), with the reason; the
    /// next delete is a new operation (titan-ops architecture/desired-and-current.md#abandoned-operation).
    public bool DeleteFailed(Guid operationId, string? reason, string? detail)
    {
        if (Status != VmStatus.Deleting || Operation.PendingId != operationId)
        {
            return false;
        }
        VmStatus resting = InstanceStatus ?? VmStatus.Unknown;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}
