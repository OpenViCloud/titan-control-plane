using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Services;

// The name and size a customer sends are checked by the command handler before it builds or renames the aggregate (titan-ops
// architecture/domain-methods.md#names-and-sizes). The endpoint validators answer 400 first from the same Domain
// constants (CreateVmValidatorTests); this is the 422 behind them, for a caller that did not come through HTTP.
// Every handler asks before it builds: InputOwnershipTests.
public class ComputeInputTests
{
    private static string Code(Action act) => Assert.Throws<BusinessException>(act).Code;

    // ---- VM name: 1 to 64 letters, digits or hyphens, starting with a letter or digit

    [Theory]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("a_b")]
    [InlineData("a b")]
    [InlineData("tên")]
    [InlineData(null)]
    public void VmName_OutsideTheRule_IsInvalidVmName(string? name)
    {
        Assert.Equal("INVALID_VM_NAME", Code(() => ComputeInput.EnsureVmName(name)));
    }

    [Fact]
    public void VmName_Takes64Characters_AndRefuses65()
    {
        ComputeInput.EnsureVmName(new string('a', Vm.NameMaxLength));
        ComputeInput.EnsureVmName("vm-1");
        Assert.Equal("INVALID_VM_NAME", Code(() => ComputeInput.EnsureVmName(new string('a', Vm.NameMaxLength + 1))));
    }

    // ---- VM disk: 0 is the image's own size; a positive number overrides it

    [Fact]
    public void VmDisk_Negative_IsInvalidVmDiskSize()
    {
        Assert.Equal("INVALID_VM_DISK_SIZE", Code(() => ComputeInput.EnsureDiskSize(-1)));
    }

    [Theory]
    [InlineData(Vm.ImageDiskSize)]
    [InlineData(1)]
    [InlineData(500)]
    public void VmDisk_TakesTheImageSize_OrAPositiveDisk(int diskSizeGb)
    {
        ComputeInput.EnsureDiskSize(diskSizeGb);
    }

    // ---- Volume: a name of 1 to 100 characters, not blank, any characters; a size above 0

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void VolumeName_Blank_IsInvalidVolumeName(string? name)
    {
        Assert.Equal("INVALID_VOLUME_NAME", Code(() => ComputeInput.EnsureVolumeName(name)));
    }

    [Fact]
    public void VolumeName_Takes100Characters_AndRefuses101()
    {
        ComputeInput.EnsureVolumeName(new string('a', Volume.NameMaxLength));
        ComputeInput.EnsureVolumeName("my data");
        Assert.Equal("INVALID_VOLUME_NAME", Code(() => ComputeInput.EnsureVolumeName(new string('a', Volume.NameMaxLength + 1))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void VolumeSize_NotAboveZero_IsInvalidVolumeSize(int sizeGb)
    {
        Assert.Equal("INVALID_VOLUME_SIZE", Code(() => ComputeInput.EnsureVolumeSize(sizeGb)));
    }

    [Fact]
    public void VolumeSize_OfOneGb_IsAccepted()
    {
        ComputeInput.EnsureVolumeSize(1);
    }

    // ---- Load balancer name: 1 to 100 letters, digits, hyphens or underscores, starting with a letter or digit

    [Theory]
    [InlineData("")]
    [InlineData("_a")]
    [InlineData("a b")]
    [InlineData(null)]
    public void LoadBalancerName_OutsideTheRule_IsInvalidLoadBalancerName(string? name)
    {
        Assert.Equal("INVALID_LOAD_BALANCER_NAME", Code(() => ComputeInput.EnsureLoadBalancerName(name)));
    }

    [Fact]
    public void LoadBalancerName_Takes100Characters_AndRefuses101()
    {
        ComputeInput.EnsureLoadBalancerName(new string('a', LoadBalancer.NameMaxLength));
        ComputeInput.EnsureLoadBalancerName("lb_1-a");
        Assert.Equal("INVALID_LOAD_BALANCER_NAME",
            Code(() => ComputeInput.EnsureLoadBalancerName(new string('a', LoadBalancer.NameMaxLength + 1))));
    }

    // ---- Snapshot name, of a VM or of a volume: 1 to 100 characters, not blank

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void VmSnapshotName_Blank_IsInvalidSnapshotName(string? name)
    {
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => ComputeInput.EnsureVmSnapshotName(name)));
    }

    [Fact]
    public void VmSnapshotName_Takes100Characters_AndRefuses101()
    {
        ComputeInput.EnsureVmSnapshotName(new string('a', VmSnapshot.NameMaxLength));
        ComputeInput.EnsureVmSnapshotName("before upgrade");
        Assert.Equal("INVALID_SNAPSHOT_NAME",
            Code(() => ComputeInput.EnsureVmSnapshotName(new string('a', VmSnapshot.NameMaxLength + 1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void VolumeSnapshotName_Blank_IsInvalidSnapshotName(string? name)
    {
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => ComputeInput.EnsureVolumeSnapshotName(name)));
    }

    [Fact]
    public void VolumeSnapshotName_Takes100Characters_AndRefuses101()
    {
        ComputeInput.EnsureVolumeSnapshotName(new string('a', VolumeSnapshot.NameMaxLength));
        ComputeInput.EnsureVolumeSnapshotName("nightly");
        Assert.Equal("INVALID_SNAPSHOT_NAME",
            Code(() => ComputeInput.EnsureVolumeSnapshotName(new string('a', VolumeSnapshot.NameMaxLength + 1))));
    }
}
