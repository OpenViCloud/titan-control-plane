using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A stored name or CIDR that breaks the Domain's rule is refused at load, not served as data: the value object's
/// Parse runs in the EF converter (TitanDbContext.ConfigureConventions), the same way a negative Rate fails
/// (titan-ops architecture/code-structure.md#logic-placement). What InMemory cannot show: the column is written
/// behind EF's back, as a migration or an operator could.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ValueObjectColumnsDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task ANetworkNameThatBreaksTheRule_FailsAtLoad_NotSilently()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "was-fine", "cluster-a");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.networks SET name = {"-bad name"} WHERE id = {network.Id}");
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => db.Networks.SingleAsync(n => n.Id == network.Id));
            Assert.Equal("INVALID_NETWORK_NAME", ex.Code);
        }
    }

    [Fact]
    public async Task AFirewallRuleCidrThatBreaksTheRule_FailsAtLoad_NotSilently()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "with-a-rule", "cluster-a");
        FirewallRule rule = network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.firewall_rules SET cidr = {"10.0.0.300/24"} WHERE id = {rule.Id}");
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => db.FirewallRules.SingleAsync(r => r.Id == rule.Id));
            Assert.Equal("INVALID_CIDR", ex.Code);
        }
    }

    // The same columns with values that keep the rule round-trip unchanged.
    [Fact]
    public async Task AValidNameAndCidr_RoundTrip()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "round_trip-01", "cluster-a");
        FirewallRule rule = network.AddFirewallRuleUnchecked(FirewallDirection.Egress, FirewallProtocol.UDP, 53, 53, "2001:db8::/32");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Network stored = await db.Networks.Include(n => n.FirewallRules).SingleAsync(n => n.Id == network.Id);
            Assert.Equal("round_trip-01", stored.Name.Value);
            Assert.Equal("2001:db8::/32", Assert.Single(stored.FirewallRules).Cidr.Value);
            Assert.Equal(53, Assert.Single(stored.FirewallRules).Ports!.Start);
        }
    }
}
