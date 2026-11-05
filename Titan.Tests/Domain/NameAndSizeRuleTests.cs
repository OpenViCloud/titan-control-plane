using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The name and size rules of a VM, a volume, a load balancer and a snapshot live in the Domain, so no path — the API,
// a handler, a restore, a test — builds one the /v1 contract never allowed (titan-ops
// architecture/code-structure.md#logic-placement). The validators answer 400 first, in their own words, from the same
// constants (CreateVmValidatorTests).
public class NameAndSizeRuleTests
{
    private static readonly Tenant Owner = TestTenant.WithRoomToSpare();

    private static Vm NewVm(string name, int diskSizeGb = 20) =>
        Vm.Create(Owner.Id, Guid.NewGuid(), name, "m", "tpl-1", "cluster-a", diskSizeGb);

    private static Vm RunningVm(Guid? networkId = null)
    {
        Vm vm = Vm.Create(Owner.Id, networkId ?? Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm-ext", "10.0.0.5");
        return vm;
    }

    private static string Code(Action act) => Assert.Throws<BusinessException>(act).Code;

    // ---- VM name: 1 to 64 letters, digits or hyphens, starting with a letter or digit

    [Theory]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("a_b")]
    [InlineData("a b")]
    [InlineData("tên")]
    public void VmCreate_WithANameOutsideTheRule_IsInvalidVmName(string name)
    {
        Assert.Equal("INVALID_VM_NAME", Code(() => NewVm(name)));
    }

    [Fact]
    public void VmCreate_Takes64Characters_AndRefuses65()
    {
        string sixtyFour = new('a', Vm.NameMaxLength);
        Assert.Equal(sixtyFour, NewVm(sixtyFour).Name);
        Assert.Equal("INVALID_VM_NAME", Code(() => NewVm(sixtyFour + "a")));
    }

    [Fact]
    public void VmRename_FollowsTheSameRule()
    {
        Vm vm = NewVm("vm-1");
        Assert.Equal("INVALID_VM_NAME", Code(() => vm.Rename("a_b")));
        Assert.Equal("vm-1", vm.Name);
        vm.Rename("vm-2");
        Assert.Equal("vm-2", vm.Name);
    }

    // ---- VM disk: 0 is the image's own size; a positive number overrides it

    [Fact]
    public void VmCreate_WithANegativeDisk_IsInvalidVmDiskSize()
    {
        Assert.Equal("INVALID_VM_DISK_SIZE", Code(() => NewVm("vm-1", -1)));
    }

    [Theory]
    [InlineData(Vm.ImageDiskSize)]
    [InlineData(1)]
    [InlineData(500)]
    public void VmCreate_TakesTheImageSize_OrAPositiveDisk(int diskSizeGb)
    {
        Assert.Equal(diskSizeGb, NewVm("vm-1", diskSizeGb).DiskSizeGb);
    }

    // ---- Volume: a name of 1 to 100 characters, not blank, any characters; a size above 0

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void VolumeCreate_WithABlankName_IsInvalidVolumeName(string? name)
    {
        Assert.Equal("INVALID_VOLUME_NAME", Code(() => Volume.Create(Owner.Id, name!, "cluster-a", "standard", 10)));
    }

    [Fact]
    public void VolumeCreate_Takes100Characters_AndRefuses101()
    {
        string hundred = new('a', Volume.NameMaxLength);
        Assert.Equal(hundred, Volume.Create(Owner.Id, hundred, "cluster-a", "standard", 10).Name);
        Assert.Equal("INVALID_VOLUME_NAME", Code(() => Volume.Create(Owner.Id, hundred + "a", "cluster-a", "standard", 10)));
        Assert.Equal("my data", Volume.Create(Owner.Id, "my data", "cluster-a", "standard", 10).Name);
    }

    // A restore builds the new volume through the same factory, with the name the customer gave it.
    [Fact]
    public void VolumeRestore_WithABlankName_IsInvalidVolumeName()
    {
        Volume source = Volume.Create(Owner.Id, "data", "cluster-a", "standard", 10);
        source.MarkCreated("vol-ext");
        VolumeSnapshot snapshot = source.WithSnapshot("nightly");
        snapshot.MarkCreated("vol-ext/s1");
        Assert.Equal("INVALID_VOLUME_NAME",
            Code(() => Volume.Create(Owner.Id, " ", "cluster-a", "standard", 10, restoredFrom: snapshot)));
        Assert.Equal("restored", Volume.Create(Owner.Id, "restored", "cluster-a", "standard", 10, restoredFrom: snapshot).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void VolumeCreate_WithASizeNotAboveZero_IsInvalidVolumeSize(int sizeGb)
    {
        Assert.Equal("INVALID_VOLUME_SIZE", Code(() => Volume.Create(Owner.Id, "data", "cluster-a", "standard", sizeGb)));
    }

    // ---- Load balancer name: 1 to 100 letters, digits, hyphens or underscores, starting with a letter or digit

    private const string Subnet = "2001:41d0:d00:d405::/64";

    private static LoadBalancer NewLoadBalancer(string name)
    {
        Guid networkId = Guid.NewGuid();
        return LoadBalancer.Create(Owner.Id, name, networkId, "net-ext", Subnet, "cluster-a", LoadBalancerVip.For(Subnet, 1),
            [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [RunningVm(networkId)]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_a")]
    [InlineData("a b")]
    public void LoadBalancerCreate_WithANameOutsideTheRule_IsInvalidLoadBalancerName(string name)
    {
        Assert.Equal("INVALID_LOAD_BALANCER_NAME", Code(() => NewLoadBalancer(name)));
    }

    [Fact]
    public void LoadBalancer_Takes100Characters_AndRefuses101_OnCreateAndRename()
    {
        string hundred = new('a', LoadBalancer.NameMaxLength);
        Assert.Equal(hundred, NewLoadBalancer(hundred).Name);
        LoadBalancer lb = NewLoadBalancer("lb_1-a");
        Assert.Equal("INVALID_LOAD_BALANCER_NAME", Code(() => NewLoadBalancer(hundred + "a")));
        Assert.Equal("INVALID_LOAD_BALANCER_NAME", Code(() => lb.Rename(hundred + "a")));
        lb.Rename(hundred);
        Assert.Equal(hundred, lb.Name);
    }

    // ---- Snapshot name, of a VM or of a volume: 1 to 100 characters

    [Fact]
    public void VmAddSnapshot_WithABlankName_OrOver100_IsInvalidSnapshotName()
    {
        Vm vm = RunningVm();
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => vm.AddSnapshot(Guid.NewGuid(), "")));
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => vm.AddSnapshot(Guid.NewGuid(), "  ")));
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => vm.AddSnapshot(Guid.NewGuid(), new string('a', VmSnapshot.NameMaxLength + 1))));
        Assert.Empty(vm.Snapshots);
        Assert.Equal("before upgrade", vm.WithSnapshot("before upgrade").Name);
    }

    [Fact]
    public void VolumeAddSnapshot_WithABlankName_OrOver100_IsInvalidSnapshotName()
    {
        Volume volume = Volume.Create(Owner.Id, "data", "cluster-a", "standard", 10);
        volume.MarkCreated("vol-ext");
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => volume.AddSnapshot(Guid.NewGuid(), "")));
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => volume.AddSnapshot(Guid.NewGuid(), "  ")));
        Assert.Equal("INVALID_SNAPSHOT_NAME", Code(() => volume.AddSnapshot(Guid.NewGuid(), new string('a', VolumeSnapshot.NameMaxLength + 1))));
        Assert.Empty(volume.Snapshots);
        Assert.Equal("nightly", volume.WithSnapshot("nightly").Name);
    }
}
