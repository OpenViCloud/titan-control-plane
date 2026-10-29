using System.Reflection;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// TenantQuota is the value object that holds all nine caps (titan-ops architecture/tenancy-and-admin.md#quota-value-object),
// and Tenant is the only thing that decides.
public class TenantQuotaTests
{
    private static TenantQuota Valid() => TenantQuota.Create(10, 1, 10, 3, 3, 20, 10, 20, 5);

    [Fact] // one case per field. A single negative case proves nothing about the other seven.
    public void Create_NegativeValue_Throws_ForEveryField()
    {
        var fields = new (string Name, Func<TenantQuota> Build)[]
        {
            ("maxVms",                        () => TenantQuota.Create(-1, 1, 10, 3, 3, 20, 10, 20, 5)),
            ("maxNetworks",                   () => TenantQuota.Create(10, -1, 10, 3, 3, 20, 10, 20, 5)),
            ("maxVolumes",                    () => TenantQuota.Create(10, 1, -1, 3, 3, 20, 10, 20, 5)),
            ("maxVmSnapshotsPerVm",           () => TenantQuota.Create(10, 1, 10, -1, 3, 20, 10, 20, 5)),
            ("maxVolumeSnapshotsPerVolume",   () => TenantQuota.Create(10, 1, 10, 3, -1, 20, 10, 20, 5)),
            ("maxFirewallRulesPerNetwork",    () => TenantQuota.Create(10, 1, 10, 3, 3, -1, 10, 20, 5)),
            ("maxPublicIpv6",                 () => TenantQuota.Create(10, 1, 10, 3, 3, 20, -1, 20, 5)),
            ("maxSshKeys",                    () => TenantQuota.Create(10, 1, 10, 3, 3, 20, 10, -1, 5)),
            ("maxLoadBalancers",              () => TenantQuota.Create(10, 1, 10, 3, 3, 20, 10, 20, -1)),
        };

        var unguarded = new List<string>();
        foreach (var (name, build) in fields)
        {
            try { build(); unguarded.Add(name); }
            catch (BusinessException ex) when (ex.Code == "INVALID_QUOTA") { }
        }

        Assert.True(unguarded.Count == 0,
            "TenantQuota.Create accepted a negative value for: " + string.Join(", ", unguarded));
    }

    [Fact] // zero is a valid cap, not "unlimited". Guarding it away would silently grant quota.
    public void Create_Zero_IsAccepted()
    {
        var q = TenantQuota.Create(0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, q.MaxVms);
        Assert.Equal(0, q.MaxVolumes);
    }

    [Fact] // a value object compares by value, not by reference
    public void SameValues_AreEqual()
    {
        Assert.Equal(Valid(), Valid());
        Assert.Equal(Valid().GetHashCode(), Valid().GetHashCode());
    }

    [Fact] // and differs when any single field differs
    public void DifferentValues_AreNotEqual()
    {
        Assert.NotEqual(Valid(), TenantQuota.Create(11, 1, 10, 3, 3, 20, 10, 20, 5));
        Assert.NotEqual(Valid(), TenantQuota.Create(10, 1, 10, 3, 3, 20, 9, 20, 5));
        Assert.NotEqual(Valid(), TenantQuota.Create(10, 1, 10, 3, 3, 20, 10, 19, 5));
        Assert.NotEqual(Valid(), TenantQuota.Create(10, 1, 10, 3, 3, 20, 10, 20, 4));
    }

    [Fact] // no back door. A positional record hands out a public `with`, which would let a
    // caller build a negative quota without ever passing through Create. A value object whose
    // invariant can be bypassed is not holding an invariant.
    public void NoPropertySetter_IsPubliclyWritable()
    {
        var writable = typeof(TenantQuota)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
            .Select(p => p.Name)
            .ToList();

        Assert.True(writable.Count == 0,
            "TenantQuota has publicly writable member(s), so `with` can bypass Create: "
            + string.Join(", ", writable));
    }

    [Fact] // the admin endpoint only carries two caps; the others must survive it
    public void WithVmAndNetworkCaps_PreservesTheOtherCaps()
    {
        var updated = Valid().WithVmAndNetworkCaps(99, 7);

        Assert.Equal(99, updated.MaxVms);
        Assert.Equal(7, updated.MaxNetworks);
        Assert.Equal(10, updated.MaxVolumes);
        Assert.Equal(3, updated.MaxVmSnapshotsPerVm);
        Assert.Equal(3, updated.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(20, updated.MaxFirewallRulesPerNetwork);
        Assert.Equal(10, updated.MaxPublicIpv6);
        Assert.Equal(20, updated.MaxSshKeys);
        Assert.Equal(5, updated.MaxLoadBalancers);
    }

    [Fact] // the non-destructive copy validates too, or it is just `with` wearing a method name
    public void WithVmAndNetworkCaps_NegativeValue_Throws()
    {
        Assert.Throws<BusinessException>(() => Valid().WithVmAndNetworkCaps(-1, 1));
        Assert.Throws<BusinessException>(() => Valid().WithVmAndNetworkCaps(1, -1));
    }

    [Fact]
    public void Default_MatchesTheConstantsThatWereInTheServices()
    {
        var d = TenantQuota.Default;
        Assert.Equal(10, d.MaxVms);
        Assert.Equal(1, d.MaxNetworks);
        Assert.Equal(10, d.MaxVolumes);
        Assert.Equal(3, d.MaxVmSnapshotsPerVm);
        Assert.Equal(3, d.MaxVolumeSnapshotsPerVolume);
        Assert.Equal(20, d.MaxFirewallRulesPerNetwork);
        Assert.Equal(10, d.MaxPublicIpv6);
        Assert.Equal(20, d.MaxSshKeys);
        Assert.Equal(5, d.MaxLoadBalancers);
    }
}
