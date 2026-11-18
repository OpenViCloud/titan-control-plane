using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// The policy a network was created with is the one read back. EF leaves out of the INSERT a value equal to the CLR
/// default (Allow) when the column has a default of its own, so the column default must never become Deny: an Allow the
/// customer asked for would be stored as Deny (titan-ops features/network/design.md#firewall-default-policy).
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class NetworkFirewallPolicyDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task AllowAsked_IsReadBackAllow_AndPoliciesNotAsked_AreReadBackDenyInboundAllowOutbound()
    {
        var owner = TestTenant.WithRoomToSpare();
        var asked = TestAggregates.Network(owner, "allow-asked", "cluster-a",
            defaultIngressPolicy: FirewallPolicy.Allow, defaultEgressPolicy: FirewallPolicy.Allow);
        var notAsked = TestAggregates.Network(owner, "not-asked", "cluster-a");
        var denyBoth = TestAggregates.Network(owner, "deny-both", "cluster-a",
            defaultIngressPolicy: FirewallPolicy.Deny, defaultEgressPolicy: FirewallPolicy.Deny);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.AddRange(asked, notAsked, denyBoth);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Network storedAsked = await db.Networks.SingleAsync(n => n.Id == asked.Id);
            Network storedNotAsked = await db.Networks.SingleAsync(n => n.Id == notAsked.Id);
            Network storedDenyBoth = await db.Networks.SingleAsync(n => n.Id == denyBoth.Id);
            Assert.Equal((FirewallPolicy.Allow, FirewallPolicy.Allow), (storedAsked.DefaultIngressPolicy, storedAsked.DefaultEgressPolicy));
            Assert.Equal((FirewallPolicy.Deny, FirewallPolicy.Allow), (storedNotAsked.DefaultIngressPolicy, storedNotAsked.DefaultEgressPolicy));
            Assert.Equal((FirewallPolicy.Deny, FirewallPolicy.Deny), (storedDenyBoth.DefaultIngressPolicy, storedDenyBoth.DefaultEgressPolicy));
        }
    }
}
