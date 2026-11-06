using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
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
        if (IsForeignStatus(word))
        {
            throw new BusinessException("INVALID_STATUS", $"'{word}' is not an Incus status.");
        }
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
    /// rests in the Incus word it holds, Unknown when it holds none.
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
}
