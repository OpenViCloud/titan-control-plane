using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    /// Start on a Frozen instance resumes it: Incus answers `start` from Frozen by thawing the guest.
    public void Start(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        if (Status == VmStatus.Starting)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A start is already in progress for this VM.");
        }
        EnsureNotInTransition();
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
        VmStatus? seen = InstanceStatus;
        Status = VmStatus.Starting;
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, seen);
    }

    /// The start command's Done: the status is the one Incus reports. Without one the old may be stale, so it is
    /// forgotten and the status is Unknown: the reconciler reports the instance next cycle.
    public bool StartSucceeded(Guid operationId, VmStatus? status, string? ipAddress, string? publicIpv6Address)
    {
        if (Status != VmStatus.Starting || Operation.PendingId != operationId)
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

    /// The start command gave up: back to the Incus status the VM holds (Unknown without one), with the reason
    /// (titan-ops architecture/desired-and-current.md#abandoned-operation).
    public bool StartFailed(Guid operationId, string? reason, string? detail)
    {
        if (Status != VmStatus.Starting || Operation.PendingId != operationId)
        {
            return false;
        }
        VmStatus resting = InstanceStatus ?? VmStatus.Unknown;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}
