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
        Status = VmStatus.Starting;
        PendingOperationId = operationId;
        PendingOperationAskedAt = DateTimeOffset.UtcNow;
    }

    /// The start command's Done: the status is the word Incus reports. Without a word the old one may be stale, so it is
    /// forgotten and the status is Unknown: the reconciler reports the instance next cycle.
    public bool StartSucceeded(Guid operationId, VmStatus? word, string? ipAddress, string? publicIpv6Address)
    {
        if (Status != VmStatus.Starting || PendingOperationId != operationId)
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

    /// The start command gave up: back to the Incus word the VM holds (Unknown without one), with the reason
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
}
