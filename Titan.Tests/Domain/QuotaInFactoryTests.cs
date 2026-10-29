using Titan.Domain.Aggregates.Compute.SshKey;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using VmSnapshotAggregate = Titan.Domain.Aggregates.Compute.VmSnapshot.VmSnapshot;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// QuotaOwnershipTests proves the check is UNREACHABLE from a service; this proves it is
// still ENFORCED, and enforced by the factory rather than by whoever remembered to ask.
//
// Every cap gets a pair: at the cap it refuses, one below it succeeds. The pair matters — a factory
// that refused unconditionally would pass the refusal half on its own, and a `>=` silently changed
// to `>` would pass the success half. Two tests, one law, neither sufficient alone.
//
// The cap value is set EXPLICITLY on the tenant instead of relying on TenantQuota.Default: a test
// that happens to match the default is measuring the default, not the rule. `0` means "none allowed"
// (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none), so these use small non-zero caps to keep both halves meaningful.
public class QuotaInFactoryTests
{
    private static Tenant TenantWith(
        int maxVms = 9, int maxNetworks = 9, int maxVolumes = 9, int maxVmSnaps = 9,
        int maxVolSnaps = 9, int maxFwRules = 9, int maxIpv6 = 9, int maxSshKeys = 9, int maxLoadBalancers = 9)
    {
        var t = TestTenant.Active("acme", "kc-1", "a@acme.com");
        t.UpdateQuota(TenantQuota.Create(maxVms, maxNetworks, maxVolumes, maxVmSnaps,
            maxVolSnaps, maxFwRules, maxIpv6, maxSshKeys, maxLoadBalancers));
        return t;
    }

    private static Vm RunningVm(Guid tenantId)
    {
        var vm = Vm.CreateUnchecked(tenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        return vm;
    }

    private static Volume AllocatedVolume(Guid tenantId)
    {
        var v = Volume.CreateUnchecked(tenantId, "vol-1", "cluster-a", "std", 20);
        v.MarkCreated("11");
        return v;
    }

    private static VolumeSnapshot BackedUpSnapshot(Tenant t)
    {
        var s = VolumeSnapshot.Create(AllocatedVolume(t.Id), t, 0, "snap-1");
        s.MarkCreated("5");
        return s;
    }

    private static Vm NewVm(Tenant t, int activeVms) =>
        Vm.Create(t, activeVms, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20, userData: null);

    private static readonly NetworkPublicIpv6 SomeIpv6 = NetworkPublicIpv6.For("2001:db8:0:100::/56", 1);

    // ── at the cap, the FACTORY refuses, with the code clients already know ──

    [Fact]
    public void VmFactory_AtTheVmCap_Refuses()
    {
        var t = TenantWith(maxVms: 2);
        Assert.Equal("VM_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(() => NewVm(t, 2)).Code);
    }

    [Fact]
    public void NetworkFactory_AtTheIpv6Cap_Refuses_OnlyWhenIpv6IsAsked()
    {
        var t = TenantWith(maxIpv6: 1);
        Assert.Equal("IPV6_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Network.Create(t, 0, 1, "net-1", "cluster-a", SomeIpv6)).Code);

        // Same tenant, same count, IPv6 not requested — the cap does not apply. This is the half
        // that keeps the assertion above from being "the factory always throws".
        Assert.NotNull(Network.Create(t, 0, 1, "net-1", "cluster-a", null));
    }

    [Fact]
    public void NetworkFactory_AtTheNetworkCap_Refuses()
    {
        var t = TenantWith(maxNetworks: 3);
        Assert.Equal("NETWORK_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Network.Create(t, 3, 0, "net-1", "cluster-a", null)).Code);
    }

    [Fact]
    public void VolumeFactory_AtTheVolumeCap_Refuses()
    {
        var t = TenantWith(maxVolumes: 4);
        Assert.Equal("VOLUME_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Volume.Create(t, 4, "vol-1", "cluster-a", "std", 20)).Code);
    }

    // A restore spends VOLUME quota, not snapshot quota — the thing being created is a volume.
    [Fact]
    public void RestoreFactory_AtTheVolumeCap_Refuses()
    {
        var t = TenantWith(maxVolumes: 4);
        var snap = BackedUpSnapshot(t);
        Assert.Equal("VOLUME_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => Volume.CreateFromSnapshot(snap, t, 4, "vol-2", "cluster-a", "std", 20)).Code);
    }

    [Fact]
    public void VmSnapshotFactory_AtThePerVmCap_Refuses()
    {
        var t = TenantWith(maxVmSnaps: 3);
        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => VmSnapshotAggregate.Create(RunningVm(t.Id), t, 3, "snap-1")).Code);
    }

    [Fact]
    public void VolumeSnapshotFactory_AtThePerVolumeCap_Refuses()
    {
        var t = TenantWith(maxVolSnaps: 2);
        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => VolumeSnapshot.Create(AllocatedVolume(t.Id), t, 2, "snap-1")).Code);
    }

    [Fact]
    public void FirewallRuleFactory_AtThePerNetworkCap_Refuses()
    {
        var t = TenantWith(maxFwRules: 5);
        Assert.Equal("FIREWALL_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => FirewallRule.Create(Guid.NewGuid(), t, 5, FirewallDirection.Ingress,
                FirewallProtocol.TCP, 80, 80, "0.0.0.0/0")).Code);
    }

    [Fact]
    public void SshKeyFactory_AtTheTenantCap_Refuses()
    {
        var t = TenantWith(maxSshKeys: 4);
        Assert.Equal("SSH_KEY_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => SshKey.Create(t, 4, "laptop", SshPublicKeyTests.Ed25519)).Code);
    }

    // ── one below the cap, every factory still works ────────────────────────────────

    [Fact]
    public void EveryFactory_OneBelowItsCap_Succeeds()
    {
        var t = TenantWith(maxVms: 2, maxNetworks: 3, maxVolumes: 4, maxVmSnaps: 3,
            maxVolSnaps: 2, maxFwRules: 5, maxIpv6: 1, maxSshKeys: 4);

        Assert.NotNull(NewVm(t, 1));
        Assert.NotNull(Network.Create(t, 2, 0, "net-2", "cluster-a", SomeIpv6));
        Assert.NotNull(Network.Create(t, 2, 0, "net-1", "cluster-a", null));
        Assert.NotNull(Volume.Create(t, 3, "vol-1", "cluster-a", "std", 20));
        Assert.NotNull(Volume.CreateFromSnapshot(BackedUpSnapshot(t), t, 3, "vol-2", "cluster-a", "std", 20));
        Assert.NotNull(VmSnapshotAggregate.Create(RunningVm(t.Id), t, 2, "snap-1"));
        Assert.NotNull(VolumeSnapshot.Create(AllocatedVolume(t.Id), t, 1, "snap-1"));
        Assert.NotNull(FirewallRule.Create(Guid.NewGuid(), t, 4, FirewallDirection.Ingress,
            FirewallProtocol.TCP, 80, 80, "0.0.0.0/0"));
        Assert.NotNull(SshKey.Create(t, 3, "laptop", SshPublicKeyTests.Ed25519));
    }

    // A cap of 0 means "none allowed", not "unlimited" (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none). Pinned
    // here because the factory is the place that reads it, and reading 0 as unlimited would hand out infinite resources at
    // exactly the moment an admin tried to cut a tenant off.
    [Fact]
    public void ACapOfZero_RefusesTheFirstOne()
    {
        var t = TenantWith(maxVms: 0);
        Assert.Equal("VM_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(() => NewVm(t, 0)).Code);
    }

    [Fact]
    public void ASshKeyCapOfZero_RefusesTheFirstKey()
    {
        var t = TenantWith(maxSshKeys: 0);
        Assert.Equal("SSH_KEY_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => SshKey.Create(t, 0, "laptop", SshPublicKeyTests.Ed25519)).Code);
    }
}
