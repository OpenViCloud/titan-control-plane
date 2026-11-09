using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    /// A stop still waits, or the VM is already stopped: a later stop is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsStopWaitingOrStopped => Status == VmStatus.Stopping || Status == VmStatus.Stopped;

    /// Intent: the VM goes Stopping under operationId and hands back the instance name the stop command acts on
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    public string Stop(Guid operationId)
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
        VmStatus? seen = InstanceStatus;
        Status = VmStatus.Stopping;
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, seen);
        return ExternalId!;
    }

    /// The stop command's Done: the status is the one Incus reports; without one, Unknown, as StartSucceeded.
    public bool StopSucceeded(Guid operationId, VmStatus? status, string? ipAddress, string? publicIpv6Address)
    {
        if (Status != VmStatus.Stopping || Operation.PendingId != operationId)
        {
            return false;
        }
        if (IsForeignStatus(status))
        {
            throw new BusinessException("INVALID_STATUS", $"'{status}' is not an Incus status.");
        }
        Operation = Operation.Done();
        if (status == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            IpAddress = ipAddress;
            PublicIpv6Address = publicIpv6Address;
            Status = status.Value;
        }
        return true;
    }

    /// The stop command gave up: back to the Incus status the VM holds (Unknown without one), with the reason.
    public bool StopFailed(Guid operationId, string? reason, string? detail)
    {
        if (Status != VmStatus.Stopping || Operation.PendingId != operationId)
        {
            return false;
        }
        VmStatus resting = InstanceStatus ?? VmStatus.Unknown;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}
