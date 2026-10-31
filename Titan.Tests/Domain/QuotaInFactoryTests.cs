using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

using Titan.Domain.Values;

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

    // A running VM already holding `snapshots` snapshots that count toward the per-VM cap (its child entities).
    private static Vm RunningVmWithSnapshots(Guid tenantId, int snapshots)
    {
        var vm = Vm.CreateUnchecked(tenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        for (int i = 0; i < snapshots; i++)
        {
            vm.AddSnapshotUnchecked($"snap-{i}").MarkCreated($"42/s{i}");
        }
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
        var s = AllocatedVolume(t.Id).AddSnapshot(t, "snap-1");
        s.MarkCreated("5");
        return s;
    }

    // A volume Incus has created holding `counting` snapshots that count toward the cap: the volume counts its own
    // snapshots (titan-ops architecture/code-structure.md#child-entity).
    private static Volume AllocatedVolumeWithSnapshots(Guid tenantId, int counting)
    {
        var v = AllocatedVolume(tenantId);
        for (int i = 0; i < counting; i++)
        {
            v.AddSnapshotUnchecked($"snap-{i}").MarkCreated($"11/s{i}");
        }
        return v;
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
            Assert.Throws<BusinessException>(() => Network.Create(t, 0, 1, NetworkName.Parse("net-1"), "cluster-a", null, SomeIpv6)).Code);

        // Same tenant, same count, IPv6 not requested — the cap does not apply. This is the half
        // that keeps the assertion above from being "the factory always throws".
        Assert.NotNull(Network.Create(t, 0, 1, NetworkName.Parse("net-1"), "cluster-a", null, null));
    }

    [Fact]
    public void NetworkFactory_AtTheNetworkCap_Refuses()
    {
        var t = TenantWith(maxNetworks: 3);
        Assert.Equal("NETWORK_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Network.Create(t, 3, 0, NetworkName.Parse("net-1"), "cluster-a", null, null)).Code);
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
            () => RunningVmWithSnapshots(t.Id, 3).AddSnapshot(t, "snap-1")).Code);
    }

    [Fact]
    public void VolumeSnapshotFactory_AtThePerVolumeCap_Refuses()
    {
        var t = TenantWith(maxVolSnaps: 2);
        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => AllocatedVolumeWithSnapshots(t.Id, 2).AddSnapshot(t, "snap-1")).Code);
    }

    [Fact]
    public void FirewallRuleFactory_AtThePerNetworkCap_Refuses()
    {
        var t = TenantWith(maxFwRules: 5);
        Assert.Equal("FIREWALL_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(
            () => UsableNetworkWithRules(t.Id, 5).AddFirewallRule(t, FirewallDirection.Ingress,
                FirewallProtocol.TCP, PortRange.Of(80, 80), FirewallCidr.Parse("0.0.0.0/0"))).Code);
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
        Assert.NotNull(Network.Create(t, 2, 0, NetworkName.Parse("net-2"), "cluster-a", null, SomeIpv6));
        Assert.NotNull(Network.Create(t, 2, 0, NetworkName.Parse("net-1"), "cluster-a", null, null));
        Assert.NotNull(Volume.Create(t, 3, "vol-1", "cluster-a", "std", 20));
        Assert.NotNull(Volume.CreateFromSnapshot(BackedUpSnapshot(t), t, 3, "vol-2", "cluster-a", "std", 20));
        Assert.NotNull(RunningVmWithSnapshots(t.Id, 2).AddSnapshot(t, "snap-1"));
        Assert.NotNull(AllocatedVolumeWithSnapshots(t.Id, 1).AddSnapshot(t, "snap-1"));
        Assert.NotNull(UsableNetworkWithRules(t.Id, 4).AddFirewallRule(t, FirewallDirection.Ingress,
            FirewallProtocol.TCP, PortRange.Of(80, 80), FirewallCidr.Parse("0.0.0.0/0")));
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

    // A network Incus has created, holding `counting` rules that count toward the cap: the network counts its own
    // rules (titan-ops architecture/code-structure.md#child-entity), so the test shapes the network, not a number.
    private static Network UsableNetworkWithRules(Guid tenantId, int counting)
    {
        var network = Network.CreateUnchecked(tenantId, "net", "cluster-a");
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        for (int i = 0; i < counting; i++)
        {
            network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 1000 + i, 1000 + i, "0.0.0.0/0")
                .MarkActive($"acl/rule-{i}");
        }
        return network;
    }
}
