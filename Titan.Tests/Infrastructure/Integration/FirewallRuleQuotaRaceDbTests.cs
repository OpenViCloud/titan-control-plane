using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The per-network rule cap is counted on the network's own rules, under a lock on the network row
/// (titan-ops architecture/code-structure.md#child-entity): two requests that both read the network one below its cap take
/// turns, and the second counts the first. Category=Integration — its own CI step; needs real Postgres, since the lock is
/// a Postgres row lock and InMemory has none (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class FirewallRuleQuotaRaceDbTests(PostgresFixture pg)
{
    private static readonly CreateFirewallRuleRequestShape OneMoreRule = new("Ingress", "TCP", 80, 80, "0.0.0.0/0");

    private sealed record CreateFirewallRuleRequestShape(string Direction, string Protocol, int? StartPort, int? EndPort, string Cidr);

    [Fact]
    public async Task TwoCreatesOneBelowTheCap_OneIs201_TheOtherIsQuotaExceeded_AndTheCapHolds()
    {
        Tenant tenant = TestTenant.Active($"fw-race-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "fw-race@titan.local");
        Network network = Network.CreateUnchecked(tenant.Id, $"net-{Guid.NewGuid():N}"[..12], "cluster-1");
        network.MarkActive($"net{Guid.NewGuid():N}"[..20], "10.0.0.0/24", "10.0.0.1");
        int cap = tenant.Quota.MaxFirewallRulesPerNetwork;
        for (int i = 0; i < cap - 1; i++)
        {
            network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 1000 + i, 1000 + i, "0.0.0.0/0")
                .MarkActive($"acl/rule-{i}");
        }
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Networks.Add(network);
            await seed.SaveChangesAsync();
        }

        // Two requests, two contexts, started together: each would read cap - 1 rules without the lock.
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        Task<Exception?> a = TryCreateAsync(first, tenant.Id, network.Id);
        Task<Exception?> b = TryCreateAsync(second, tenant.Id, network.Id);
        Exception?[] outcomes = await Task.WhenAll(a, b);

        Exception? refused = Assert.Single(outcomes, o => o != null);
        Assert.Single(outcomes, o => o == null);
        BusinessException quota = Assert.IsType<BusinessException>(refused);
        Assert.Equal("FIREWALL_QUOTA_EXCEEDED", quota.Code);

        await using TitanDbContext read = pg.NewDbContext();
        Network stored = (await new NetworkRepository(read).FindByIdAsync(network.Id))!;
        Assert.Equal(cap, stored.FirewallRuleQuotaUsage);
        Assert.Equal(cap, stored.FirewallRules.Count);
        Assert.Equal(1, await read.ActivityEvents.CountAsync(e => e.Action == "firewall_rule.create_requested" && e.ResourceType == "firewall_rule"
                                                                  && stored.FirewallRules.Select(r => r.Id).Contains(e.ResourceId)));
    }

    // The foreign key is the database's half of "a rule is the network's": a row pointing at no network is refused.
    [Fact]
    public async Task ARuleWhoseNetworkDoesNotExist_IsRefusedByTheDatabase()
    {
        Network orphanHolder = Network.CreateUnchecked(Guid.NewGuid(), $"gone-{Guid.NewGuid():N}"[..12], "cluster-1");
        FirewallRule orphan = orphanHolder.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");

        await using TitanDbContext db = pg.NewDbContext();
        db.FirewallRules.Add(orphan); // the network itself is never added

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task<Exception?> TryCreateAsync(TitanDbContext db, Guid tenantId, Guid networkId)
    {
        var caller = Callers.TenantMember(tenantId);
        var handler = new CreateFirewallRuleCommandHandler(
            new NetworkRepository(db), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        try
        {
            await handler.HandleAsync(new CreateFirewallRuleCommand(
                networkId, OneMoreRule.Direction, OneMoreRule.Protocol, OneMoreRule.StartPort, OneMoreRule.EndPort, OneMoreRule.Cidr));
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
