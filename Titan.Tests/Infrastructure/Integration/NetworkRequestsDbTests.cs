using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Services;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md for networks and firewall rules: the row
/// lock that makes two requests on one rule take turns (§one-key-per-request), and the stuck-request query joining a
/// network's and a rule's request to its command row (§lost-and-stale). Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class NetworkRequestsDbTests(PostgresFixture pg)
{
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    // A network Incus holds, with one rule in its ACL; nothing waits on either.
    private async Task<(Network Network, FirewallRule Rule)> StoredNetworkWithARuleAsync()
    {
        Network network = Network.Create(Tenant, NetworkName.Parse($"net-{Guid.NewGuid():N}"[..14]), "cluster-a", null,
            wantsPublicIpv6: false);
        network.ReportDone(network.Operation.PendingId!.Value, $"net-{network.Id:N}", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        FirewallRule rule = network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null,
            FirewallCidr.Parse("10.0.0.0/8"));
        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, $"acl-{network.Id:N}/1", FirewallRuleState.enabled);
        await using TitanDbContext db = pg.NewDbContext();
        db.Networks.Add(network);
        await db.SaveChangesAsync();
        return (network, rule);
    }

    private async Task DeleteRuleThroughTheHandlerAsync(Guid networkId, Guid ruleId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new DeleteFirewallRuleCommandHandler(new NetworkRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new DeleteFirewallRuleCommand(networkId, ruleId));
    }

    private async Task DeleteNetworkThroughTheHandlerAsync(Guid networkId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var networks = new NetworkRepository(db);
        var handler = new DeleteNetworkCommandHandler(networks, caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller), new NetworkDeletion(networks));
        await handler.HandleAsync(new DeleteNetworkCommand(networkId));
    }

    // The second request waits on the network's row lock, then sees the first waiting and is a repeat: one request, one
    // command row, neither scope fails (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task TwoDeletesOfOneRule_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        (Network network, FirewallRule rule) = await StoredNetworkWithARuleAsync();

        await Task.WhenAll(DeleteRuleThroughTheHandlerAsync(network.Id, rule.Id), DeleteRuleThroughTheHandlerAsync(network.Id, rule.Id));

        await using TitanDbContext db = pg.NewDbContext();
        Network stored = await db.Networks.Include(n => n.FirewallRules).SingleAsync(n => n.Id == network.Id);
        Assert.Equal(FirewallRuleOperationKind.Delete, stored.FirewallRules.Single().Operation.Kind!.Value);
        int rows = await db.Set<ProviderCommand>().CountAsync(c => c.Key.StartsWith($"firewall-rule-delete-{rule.Id}-"));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task AStuckNetworkRequest_AndAStuckRuleRequest_AreListedWithTheirCommandRowsStatus()
    {
        (Network leaving, _) = await StoredNetworkWithARuleAsync();
        (Network kept, FirewallRule rule) = await StoredNetworkWithARuleAsync();
        await DeleteNetworkThroughTheHandlerAsync(leaving.Id);
        await DeleteRuleThroughTheHandlerAsync(kept.Id, rule.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.networks SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE id = {leaving.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.firewall_rules SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE id = {rule.Id}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest network = Assert.Single(stuck, s => s.Id == leaving.Id);
        Assert.Equal(("network", "Delete", "Pending", true), (network.Resource, network.Kind, network.CommandStatus, network.CommandIsOpen));
        StuckRequest firewallRule = Assert.Single(stuck, s => s.Id == rule.Id);
        Assert.Equal(("firewall-rule", "Delete", "Pending", true),
            (firewallRule.Resource, firewallRule.Kind, firewallRule.CommandStatus, firewallRule.CommandIsOpen));
        Assert.DoesNotContain(stuck, s => s.Id == kept.Id);
    }
}
