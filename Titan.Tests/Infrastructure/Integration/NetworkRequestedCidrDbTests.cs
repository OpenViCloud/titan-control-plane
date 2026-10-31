using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The range the customer asked for lives in its own column, requested_cidr, from the request on
/// (titan-ops features/network/user-stories.md#us-net-intent-02); the address the worker reports stays in cidr, null
/// until then. What InMemory cannot show: the converter on the new column and the two-column query on Postgres.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class NetworkRequestedCidrDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task ARequestedCidr_RoundTrips_AndIsSeenByTheOverlapCheck_WhileTheCreateWaits()
    {
        Guid tenant = Guid.NewGuid();
        var waiting = Network.CreateUnchecked(tenant, "waiting", "cluster-a", requestedCidr: "10.77.0.0/24");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(waiting);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Network stored = await db.Networks.SingleAsync(n => n.Id == waiting.Id);
            Assert.Equal(Ipv4Cidr.Parse("10.77.0.0/24"), stored.RequestedCidr);
            Assert.Null(stored.Cidr);

            NetworkRepository repo = new(db);
            Assert.True(await repo.HasCidrOverlapAsync(tenant, Ipv4Cidr.Parse("10.77.0.128/25")));
            Assert.False(await repo.HasCidrOverlapAsync(tenant, Ipv4Cidr.Parse("10.78.0.0/24")));
        }
    }

    // A report that carries no cidr and gateway (both are optional): the columns stay NULL, never ''.
    [Fact]
    public async Task MarkCreatedWithoutAnAddress_StoresNull_NotAnEmptyString()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "no-address", "cluster-a");
        network.MarkCreated("ext-no-address", null, null);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            List<int> nullRows = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM core.networks WHERE id = {0} AND cidr IS NULL AND gateway IS NULL", network.Id)
                .ToListAsync();
            Assert.Equal(1, nullRows.Single());
        }
    }

    // Same rule as name and firewall_rules.cidr (ValueObjectColumnsDbTests): a stored value that breaks the rule is
    // refused at load, not served.
    [Fact]
    public async Task ARequestedCidrThatBreaksTheRule_FailsAtLoad_NotSilently()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "was-fine", "cluster-a", requestedCidr: "10.0.0.0/24");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.networks SET requested_cidr = {"10.0.0.300/24"} WHERE id = {network.Id}");
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => db.Networks.SingleAsync(n => n.Id == network.Id));
            Assert.Equal("INVALID_CIDR", ex.Code);
        }
    }
}
