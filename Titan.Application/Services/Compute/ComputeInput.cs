using Titan.Domain.Exceptions;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;

namespace Titan.Application.Services.Compute;

/// The shape of a name or a size a customer sends, checked by the command handler before the aggregate is built or
/// renamed (titan-ops architecture/domain-methods.md#names-and-sizes); the handlers ask before they load anything, except
/// the restore, which checks the source volume's size once it has the volume. The rule itself is a predicate on the Domain type, which
/// also holds the constants the endpoint validators read; the answer is the same 422 the aggregate used to give.
/// Guard: InputOwnershipTests.EveryCreator_ChecksTheInputFirst.
public static class ComputeInput
{
    public static void EnsureVmName(string? name)
    {
        if (!VmAggregate.IsAName(name))
        {
            throw new BusinessException("INVALID_VM_NAME",
                $"A VM name is 1 to {VmAggregate.NameMaxLength} letters, digits or hyphens, starting with a letter or digit.");
        }
    }

    public static void EnsureDiskSize(int diskSizeGb)
    {
        if (!VmAggregate.IsADiskSize(diskSizeGb))
        {
            throw new BusinessException("INVALID_VM_DISK_SIZE",
                $"A VM disk is {VmAggregate.ImageDiskSize} (the image's own size) or a positive number of GB.");
        }
    }

    public static void EnsureVolumeName(string? name)
    {
        if (!VolumeAggregate.IsAName(name))
        {
            throw new BusinessException("INVALID_VOLUME_NAME",
                $"A volume name is 1 to {VolumeAggregate.NameMaxLength} characters, not blank.");
        }
    }

    public static void EnsureVolumeSize(int sizeGb)
    {
        if (!VolumeAggregate.IsASize(sizeGb))
        {
            throw new BusinessException("INVALID_VOLUME_SIZE", "A volume is at least 1 GB.");
        }
    }

    public static void EnsureLoadBalancerName(string? name)
    {
        if (!LoadBalancerAggregate.IsAName(name))
        {
            throw new BusinessException("INVALID_LOAD_BALANCER_NAME",
                $"A load balancer name is 1 to {LoadBalancerAggregate.NameMaxLength} letters, digits, hyphens or underscores, starting with a letter or digit.");
        }
    }

    public static void EnsureVmSnapshotName(string? name)
    {
        if (!VmAggregate.IsASnapshotName(name))
        {
            throw new BusinessException("INVALID_SNAPSHOT_NAME",
                $"A snapshot name is 1 to {VmSnapshotEntity.NameMaxLength} characters, not blank.");
        }
    }

    public static void EnsureVolumeSnapshotName(string? name)
    {
        if (!VolumeAggregate.IsASnapshotName(name))
        {
            throw new BusinessException("INVALID_SNAPSHOT_NAME",
                $"A snapshot name is 1 to {VolumeSnapshotEntity.NameMaxLength} characters, not blank.");
        }
    }
}
