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
        string? userData = null,
        int stopTimeoutSeconds = DefaultStopTimeoutSeconds)
    {
        var vm = new Vm
        {
            TenantId = tenantId,
            NetworkId = networkId,
            Name = name,
            VmOfferingId = vmOfferingId,
            TemplateId = templateId,
            ClusterId = clusterId,
            DiskSizeGb = diskSizeGb,
            UserData = userData,
            StopTimeoutSeconds = stopTimeoutSeconds,
        };
        StartItsCreate(vm);
        return vm;
    }

    /// A copy of source that an admin restores from one of source's backup archives
    /// (titan-ops features/backup/design.md#restore-as-new-resource): a new VM of the same tenant, sized and placed as
    /// source is, under its own name. The worker imports the archive as this VM's instance and reports through the
    /// create, so it waits in Creating like a VM made by Create. Only a source the Backups page lists is restored: one
    /// that has an instance.
    public static Vm RestoreFromBackup(Vm source, string name)
    {
        if (!source.HasAnInstance)
        {
            throw new BusinessException("BACKUP_SOURCE_INVALID_STATE", "Only a VM that still has an instance can be restored from a backup.");
        }
        var vm = new Vm
        {
            TenantId = source.TenantId,
            NetworkId = source.NetworkId,
            Name = name,
            VmOfferingId = source.VmOfferingId,
            TemplateId = source.TemplateId,
            ClusterId = source.ClusterId,
            DiskSizeGb = source.DiskSizeGb,
            UserData = source.UserData,
            StopTimeoutSeconds = source.StopTimeoutSeconds,
            RestoredFromBackup = true,
        };
        StartItsCreate(vm);
        return vm;
    }

    /// What every new VM starts as, whichever factory builds it: its own id, and its create as the operation it waits
    /// on, which only the create command's report ends.
    private static void StartItsCreate(Vm vm)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        vm.Id = Guid.NewGuid();
        vm.Status = VmStatus.Creating;
        vm.CreatedAt = now;
        vm.Operation = VmOperation.None.Asked(Guid.NewGuid(), now, null);
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
