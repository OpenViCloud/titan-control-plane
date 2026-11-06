using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    public void Stop(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        if (Status == VmStatus.Stopping)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop is already in progress for this VM.");
        }
        EnsureNotInTransition();
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

    /// The stop command's Done: the status is the word Incus reports; without a word, Unknown, as StartSucceeded.
    public bool StopSucceeded(Guid operationId, VmStatus? word, string? ipAddress, string? publicIpv6Address)
    {
        if (Status != VmStatus.Stopping || PendingOperationId != operationId)
        {
            return false;
        }
        if (IsForeignStatus(word))
        {
            throw new BusinessException("INVALID_STATUS", $"'{word}' is not an Incus status.");
        }
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

    /// The stop command gave up: back to the Incus word the VM holds (Unknown without one), with the reason.
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
}
