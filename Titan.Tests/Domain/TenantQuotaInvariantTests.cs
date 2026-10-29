using Titan.Domain.Exceptions;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// The quota RULE lives on the aggregate that owns the NUMBER (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
// These pin the rule itself; that every factory asks it is pinned by QuotaInFactoryTests.
public class TenantQuotaInvariantTests
{
    private static TenantAggregate NewTenant() =>
        TestTenant.Active("acme", "kc-1", "a@b.c");

    // Each row: the guard, the count that must be refused at, and the error code callers already
    // depend on. The codes are load-bearing — the portal branches on res.data.code.
    public static TheoryData<string, Action<TenantAggregate, int>, int, string> Guards() => new()
    {
        { "vm",              (t, n) => t.EnsureCanCreateVm(n),              10, "VM_QUOTA_EXCEEDED" },
        { "network",         (t, n) => t.EnsureCanCreateNetwork(n),          1, "NETWORK_QUOTA_EXCEEDED" },
        { "volume",          (t, n) => t.EnsureCanCreateVolume(n),          10, "VOLUME_QUOTA_EXCEEDED" },
        { "vmSnapshot",      (t, n) => t.EnsureCanCreateVmSnapshot(n),       3, "VM_SNAPSHOT_QUOTA_EXCEEDED" },
        { "volumeSnapshot",  (t, n) => t.EnsureCanCreateVolumeSnapshot(n),   3, "VOLUME_SNAPSHOT_QUOTA_EXCEEDED" },
        { "firewallRule",    (t, n) => t.EnsureCanCreateFirewallRule(n),    20, "FIREWALL_QUOTA_EXCEEDED" },
        { "publicIpv6",      (t, n) => t.EnsureCanEnablePublicIpv6(n),      10, "IPV6_QUOTA_EXCEEDED" },
    };

    [Theory] // at the cap, creation is refused, with the code callers already branch on
    [MemberData(nameof(Guards))]
    public void AtCap_Throws_WithTheEstablishedCode(
        string label, Action<TenantAggregate, int> guard, int cap, string expectedCode)
    {
        var ex = Assert.Throws<BusinessException>(() => guard(NewTenant(), cap));
        Assert.Equal(expectedCode, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    [Theory] // and one below the cap is allowed. This is the half that catches `>` vs `>=`:
    // an off-by-one guard passes the test above and fails this one.
    [MemberData(nameof(Guards))]
    public void OneBelowCap_IsAllowed(
        string label, Action<TenantAggregate, int> guard, int cap, string expectedCode)
    {
        guard(NewTenant(), cap - 1);
        Assert.False(string.IsNullOrWhiteSpace(label + expectedCode));
    }

    [Theory] // above the cap too, not only exactly at it
    [MemberData(nameof(Guards))]
    public void AboveCap_Throws(
        string label, Action<TenantAggregate, int> guard, int cap, string expectedCode)
    {
        var ex = Assert.Throws<BusinessException>(() => guard(NewTenant(), cap + 5));
        Assert.Equal(expectedCode, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    [Fact] // zero means none allowed, not unlimited. Reading it as unlimited would hand a
    // tenant infinite resources at the exact moment an admin tried to cut them off.
    public void ZeroCap_RefusesTheFirstOne()
    {
        var tenant = NewTenant();
        tenant.UpdateQuota(Titan.Domain.Aggregates.Platform.Tenant.TenantQuota.Create(0, 0, 0, 0, 0, 0, 0, 0, 0));

        Assert.Equal("VM_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(() => tenant.EnsureCanCreateVm(0)).Code);
        Assert.Equal("VOLUME_QUOTA_EXCEEDED", Assert.Throws<BusinessException>(() => tenant.EnsureCanCreateVolume(0)).Code);
    }

    [Fact] // the two-cap admin path must not wipe the other five
    public void UpdateQuota_ViaVmAndNetworkCaps_KeepsTheOtherFive()
    {
        var tenant = NewTenant();
        tenant.UpdateQuota(tenant.Quota.WithVmAndNetworkCaps(50, 9));

        Assert.Equal(50, tenant.Quota.MaxVms);
        Assert.Equal(9, tenant.Quota.MaxNetworks);
        Assert.Equal(10, tenant.Quota.MaxVolumes);
        Assert.Equal(3, tenant.Quota.MaxVmSnapshotsPerVm);
        Assert.Equal(20, tenant.Quota.MaxFirewallRulesPerNetwork);
        Assert.Equal(10, tenant.Quota.MaxPublicIpv6);
    }

    [Fact]
    public void NewTenant_GetsTheFormerServiceConstants()
    {
        var q = NewTenant().Quota;
        Assert.Equal(10, q.MaxVms);
        Assert.Equal(1, q.MaxNetworks);
        Assert.Equal(10, q.MaxVolumes);
        Assert.Equal(3, q.MaxVmSnapshotsPerVm);
        Assert.Equal(3, q.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(20, q.MaxFirewallRulesPerNetwork);
        Assert.Equal(10, q.MaxPublicIpv6);
    }
}
