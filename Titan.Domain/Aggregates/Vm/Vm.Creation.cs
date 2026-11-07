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
            VmOfferingId = vmOfferingId,
            TemplateId = templateId,
            ClusterId = clusterId,
            DiskSizeGb = diskSizeGb,
            UserData = userData,
            CreatedAt = now,
        };
        // The create is an operation like the others: the create command's Done ends it.
        vm.Operation = VmOperation.None.Asked(Guid.NewGuid(), now, null);
        return vm;
    }

    /// The create command's Done: Incus holds the instance under externalId. Without a status the worker did not read
    /// it, so the status is Unknown until the next observation. A Done must name the instance, or nothing would be
    /// left to delete it through; a worker that sends none has a bug, and does not retry a 422.
    public bool CreateSucceeded(Guid operationId, string? externalId, VmStatus? status, string? ipAddress,
        string? publicIpv6Address)
    {
        if (Status != VmStatus.Creating || Operation.PendingId != operationId)
        {
            return false;
        }
        if (string.IsNullOrEmpty(externalId))
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
        if (IsForeignStatus(status))
        {
            throw new BusinessException("INVALID_STATUS", $"'{status}' is not an Incus status.");
        }
        ExternalId = externalId;
        IpAddress = ipAddress;
        PublicIpv6Address = publicIpv6Address;
        Operation = Operation.Done();
        if (status == null)
        {
            Status = VmStatus.Unknown;
        }
        else
        {
            Status = status.Value;
        }
        return true;
    }

    /// The create command gave up. Before Incus held an instance the VM is Failed; after (the report names it) it
    /// rests in the Incus status it holds, Unknown when it holds none.
    public bool CreateFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (Status != VmStatus.Creating || Operation.PendingId != operationId)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(externalId))
        {
            ExternalId = externalId;
        }
        VmStatus resting = InstanceStatus ?? VmStatus.Unknown;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (ExternalId == null)
        {
            Status = VmStatus.Failed;
        }
        else
        {
            Status = resting;
        }
        return true;
    }
}
