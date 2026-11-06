using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    /// A delete was asked and still waits, or the VM is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == VmStatus.Deleting || IsDeleted;

    /// A VM with a provider id is refused while an operation runs, while a snapshot create or revert runs, and once it
    /// is deleted. Otherwise its status says how it goes: Deleting, the worker force-stops whatever
    /// status Incus reports and deletes the instance (an instance already gone counts as deleted); or Deleted at once,
    /// for a VM that never reached the provider, which has no command and no observation coming
    /// (titan-ops architecture/desired-and-current.md#delete-locally). A create still waiting without a provider id may
    /// yet instantiate, so it is refused. Internal: VmDeletion is the only way in
    /// (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    internal void Delete(Guid operationId)
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

        // A stop or start still waiting finishes first: its report ends it, and only then is a delete asked
        // (titan-ops features/vm/design.md#conflict-and-delete-wins).
        EnsureNotInTransition();
        EnsureOnTheProvider();
        Status = VmStatus.Deleting;
        PendingOperationId = operationId;
        PendingOperationAskedAt = DateTimeOffset.UtcNow;
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

    /// The delete command gave up: back to the Incus word the VM holds (Unknown without one), with the reason; the
    /// next delete is a new operation (titan-ops architecture/desired-and-current.md#abandoned-operation).
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
}
