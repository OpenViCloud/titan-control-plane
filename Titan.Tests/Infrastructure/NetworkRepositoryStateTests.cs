using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

using Titan.Domain.Values;

namespace Titan.Tests.Infrastructure;

// A delete request sets DesiredState = Deleted and leaves CurrentState alone; what these tests catch is a
// repository predicate that forgets DesiredState.
//
// Silent about: the filtered unique index (InMemory does not enforce indexes); the index filter's column name
// is pinned by NetworkConfiguration and exercised only on Postgres in CI.
public class NetworkRepositoryStateTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"network-state-{Guid.NewGuid()}")
            .Options);

    private static Network Active(string name, string? cidr)
    {
        var network = Network.CreateUnchecked(Tenant, name, "vn-hcm-1");
        network.MarkActive($"ext-{name}", cidr, "10.0.0.1");
        return network;
    }

    private static Network CreateWaiting(string name, string requestedCidr) =>
        Network.CreateUnchecked(Tenant, name, "vn-hcm-1", requestedCidr: requestedCidr);

    private static Task<bool> Overlaps(TitanDbContext db, string cidr) =>
        new NetworkRepository(db).HasCidrOverlapAsync(Tenant, Ipv4Cidr.Parse(cidr));

    private static Network DeleteRequested(string name, string cidr)
    {
        var network = Active(name, cidr);
        network.RequestDelete();
        return network;
    }

    // Counted by occupancy (titan-ops features/network/design.md#network-lifecycle): what Incus holds — whatever its word —
    // or is about to hold. A delete in flight, a network Incus no longer has and a create that gave up before Incus
    // had it do not count.
    [Fact]
    public async Task Quota_CountsWhatIncusHoldsOrIsAboutTo_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        using var db = NewDb();
        var errored = Active("errored", "10.3.0.0/24");
        errored.MarkObserved(NetworkStatus.Errored);
        var lost = Active("lost", "10.4.0.0/24");
        lost.MarkNotFound();
        var failed = Network.CreateUnchecked(Tenant, "failed", "vn-hcm-1");
        failed.MarkRequestFailed(null, null, null, "reason", "detail");
        db.Networks.AddRange(
            Network.CreateUnchecked(Tenant, "creating", "vn-hcm-1"),
            Active("active", "10.1.0.0/24"),
            DeleteRequested("leaving", "10.2.0.0/24"),
            errored, lost, failed);
        await db.SaveChangesAsync();

        int count = await new NetworkRepository(db).CountActiveByTenantAsync(Tenant);

        Assert.Equal(3, count);
    }

    // titan-ops features/network/design.md#network-lifecycle — a network whose delete is in flight still holds its
    // CIDR, like its name: the network is still on the provider until it confirms, and handing the range
    // out again could overlap there.
    [Fact]
    public async Task CidrOverlap_StillSeesANetworkWhoseDeleteIsInFlight()
    {
        using var db = NewDb();
        db.Networks.Add(DeleteRequested("leaving", "10.2.0.0/24"));
        await db.SaveChangesAsync();

        bool overlaps = await new NetworkRepository(db).HasCidrOverlapAsync(Tenant, Ipv4Cidr.Parse("10.2.0.0/24"));

        Assert.True(overlaps);
    }

    // A network Incus holds whose report carried no address holds no range; a reported address that is not an IPv4
    // CIDR is kept verbatim (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim) and
    // holds none either. Neither makes the check throw.
    [Fact]
    public async Task CidrOverlap_IgnoresANetworkIncusHoldsWithoutAnIpv4Cidr()
    {
        using var db = NewDb();
        db.Networks.AddRange(Active("no-range", null), Active("odd-word", "fd00::/64"), Active("ranged", "10.1.0.0/24"));
        await db.SaveChangesAsync();

        bool overlapsTheRangedOne = await Overlaps(db, "10.1.0.0/25");
        bool overlapsNothing = await Overlaps(db, "10.9.0.0/24");

        Assert.True(overlapsTheRangedOne);
        Assert.False(overlapsNothing);
    }

    // titan-ops features/network/user-stories.md#us-net-intent-02 — the range the customer asked for is held from the
    // request on: a second create of the same tenant meets it while the first is still waiting for Incus.
    [Fact]
    public async Task CidrOverlap_SeesTheRequestedCidrOfACreateStillWaiting()
    {
        using var db = NewDb();
        db.Networks.Add(CreateWaiting("waiting", "10.7.0.0/24"));
        await db.SaveChangesAsync();

        Assert.True(await Overlaps(db, "10.7.0.0/24"));
        Assert.True(await Overlaps(db, "10.7.0.128/25"));
        Assert.False(await Overlaps(db, "10.8.0.0/24"));
    }

    // A create that gave up before Incus had the network holds nothing: the tenant may ask for the range again.
    [Fact]
    public async Task CidrOverlap_IgnoresTheRequestedCidrOfACreateThatGaveUp()
    {
        using var db = NewDb();
        var failed = CreateWaiting("failed", "10.7.0.0/24");
        failed.MarkRequestFailed(null, null, null, "reason", "detail");
        db.Networks.Add(failed);
        await db.SaveChangesAsync();

        Assert.False(await Overlaps(db, "10.7.0.0/24"));
    }

    // Once Incus answered, its word is the range — even when it differs from what was asked for.
    [Fact]
    public async Task CidrOverlap_ReadsTheObservedCidr_OnceIncusHoldsTheNetwork()
    {
        using var db = NewDb();
        var created = CreateWaiting("created", "10.7.0.0/24");
        created.MarkCreated("ext-created", "10.9.0.0/24", "10.9.0.1");
        db.Networks.Add(created);
        await db.SaveChangesAsync();

        Assert.True(await Overlaps(db, "10.9.0.0/24"));
        Assert.False(await Overlaps(db, "10.7.0.0/24"));
    }

    [Fact]
    public async Task CidrOverlap_IsFreedOnceTheNetworkIsDeleted()
    {
        using var db = NewDb();
        var gone = DeleteRequested("gone", "10.2.0.0/24");
        gone.MarkDeleted();
        db.Networks.Add(gone);
        await db.SaveChangesAsync();

        bool overlaps = await new NetworkRepository(db).HasCidrOverlapAsync(Tenant, Ipv4Cidr.Parse("10.2.0.0/24"));

        Assert.False(overlaps);
    }

    [Fact]
    public async Task CidrOverlap_StillSeesAnActiveNetwork()
    {
        using var db = NewDb();
        db.Networks.Add(Active("active", "10.1.0.0/24"));
        await db.SaveChangesAsync();

        bool overlaps = await new NetworkRepository(db).HasCidrOverlapAsync(Tenant, Ipv4Cidr.Parse("10.1.0.0/25"));

        Assert.True(overlaps);
    }

    // Occupancy (titan-ops features/network/design.md#network-lifecycle): a network Incus holds keeps its range whatever
    // its word; one Incus no longer has frees it.
    [Fact]
    public async Task CidrOverlap_SeesAnErroredNetwork_ButNotOneIncusNoLongerHas()
    {
        using var db = NewDb();
        var errored = Active("errored", "10.5.0.0/24");
        errored.MarkObserved(NetworkStatus.Errored);
        var lost = Active("lost", "10.6.0.0/24");
        lost.MarkNotFound();
        db.Networks.AddRange(errored, lost);
        await db.SaveChangesAsync();
        var repo = new NetworkRepository(db);

        Assert.True(await repo.HasCidrOverlapAsync(Tenant, Ipv4Cidr.Parse("10.5.0.0/24")));
        Assert.False(await repo.HasCidrOverlapAsync(Tenant, Ipv4Cidr.Parse("10.6.0.0/24")));
    }

    [Fact]
    public async Task Name_StaysTakenWhileTheDeleteIsInFlight_AndIsFreedOnceDeleted()
    {
        using var db = NewDb();
        var gone = DeleteRequested("gone", "10.4.0.0/24");
        gone.MarkDeleted();
        db.Networks.AddRange(DeleteRequested("leaving", "10.2.0.0/24"), gone);
        await db.SaveChangesAsync();
        var repo = new NetworkRepository(db);

        Assert.True(await repo.ExistsByNameAsync(Tenant, NetworkName.Parse("leaving")));
        Assert.False(await repo.ExistsByNameAsync(Tenant, NetworkName.Parse("gone")));
    }
}
