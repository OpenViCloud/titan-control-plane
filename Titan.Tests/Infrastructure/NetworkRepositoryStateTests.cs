using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

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

    private static Network Active(string name, string cidr)
    {
        var network = Network.CreateUnchecked(Tenant, name, "vn-hcm-1");
        network.MarkActive($"ext-{name}", cidr, "10.0.0.1");
        return network;
    }

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
        failed.MarkRequestFailed("reason", "detail");
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

        bool overlaps = await new NetworkRepository(db).HasCidrOverlapAsync(Tenant, "10.2.0.0/24");

        Assert.True(overlaps);
    }

    // A network created without a CIDR is stored with an empty one. It holds no range, so the overlap check
    // skips it instead of parsing it.
    [Fact]
    public async Task CidrOverlap_IgnoresANetworkStoredWithAnEmptyCidr()
    {
        using var db = NewDb();
        db.Networks.AddRange(Active("no-range", ""), Active("ranged", "10.1.0.0/24"));
        await db.SaveChangesAsync();

        NetworkRepository repo = new(db);
        bool overlapsTheRangedOne = await repo.HasCidrOverlapAsync(Tenant, "10.1.0.0/25");
        bool overlapsNothing = await repo.HasCidrOverlapAsync(Tenant, "10.9.0.0/24");

        Assert.True(overlapsTheRangedOne);
        Assert.False(overlapsNothing);
    }

    [Fact]
    public async Task CidrOverlap_IsFreedOnceTheNetworkIsDeleted()
    {
        using var db = NewDb();
        var gone = DeleteRequested("gone", "10.2.0.0/24");
        gone.MarkDeleted();
        db.Networks.Add(gone);
        await db.SaveChangesAsync();

        bool overlaps = await new NetworkRepository(db).HasCidrOverlapAsync(Tenant, "10.2.0.0/24");

        Assert.False(overlaps);
    }

    [Fact]
    public async Task CidrOverlap_StillSeesAnActiveNetwork()
    {
        using var db = NewDb();
        db.Networks.Add(Active("active", "10.1.0.0/24"));
        await db.SaveChangesAsync();

        bool overlaps = await new NetworkRepository(db).HasCidrOverlapAsync(Tenant, "10.1.0.0/25");

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

        Assert.True(await repo.HasCidrOverlapAsync(Tenant, "10.5.0.0/24"));
        Assert.False(await repo.HasCidrOverlapAsync(Tenant, "10.6.0.0/24"));
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

        Assert.True(await repo.ExistsByNameAsync(Tenant, "leaving"));
        Assert.False(await repo.ExistsByNameAsync(Tenant, "gone"));
    }
}
