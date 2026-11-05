using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with networks and firewall rules waiting on a request under the old column meets the migration that gives
/// each request its kind and time (titan-ops architecture/resource-lifecycle.md#shape): the kind is read from the desired
/// existence as Network.Transition and FirewallRule.Transition read it, a create whose command already reported the
/// provider id ends, an open command takes the request's key and id (a rule's under `firewall-rule-`), and the view
/// lists both tables. The old column name and keys below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class NetworkPendingRequestMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261103044539_VolumePendingRequest";

    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task MigrateToAsync(string migration)
    {
        await using TitanDbContext db = NewDbContext();
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task MigrateToLatestAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // A network row as the image before this migration wrote it: the request's id in pending_intent_id, its kind implied
    // by the desired existence.
    private async Task<Guid> SeedNetworkAsync(string currentExistence, string desiredExistence, bool hasExternalId, bool waiting)
    {
        Guid id = Guid.NewGuid();
        Guid? intent = waiting ? Guid.NewGuid() : null;
        string? externalId = hasExternalId ? $"net-{id:N}" : null;
        string? currentState = currentExistence == "Exists" ? "Created" : null;
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.networks (id, tenant_id, name, cluster_id, current_state, current_existence, desired_existence,
                           external_id, created_at, pending_intent_id)
VALUES ({id}, {Guid.NewGuid()}, {"net-" + id.ToString("N")[..8]}, 'cluster-a', {currentState}, {currentExistence},
        {desiredExistence}, {externalId}, now(), {intent})");
        return id;
    }

    private async Task<Guid> SeedRuleAsync(Guid networkId, string currentExistence, string desiredExistence, bool hasExternalId)
    {
        Guid id = Guid.NewGuid();
        string? externalId = hasExternalId ? $"acl/r-{id.ToString("N")[..6]}" : null;
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.firewall_rules (id, network_id, tenant_id, direction, protocol, start_port, end_port, cidr,
                                 current_existence, desired_existence, external_id, created_at, pending_intent_id)
VALUES ({id}, {networkId}, {Guid.NewGuid()}, 'Ingress', 'TCP', 22, 22, '10.0.0.0/8', {currentExistence},
        {desiredExistence}, {externalId}, now(), {Guid.NewGuid()})");
        return id;
    }

    private async Task AddOpenCommandAsync(string key, string kind)
    {
        await using TitanDbContext seed = NewDbContext();
        await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, key, kind, "cluster-a", "{}");
    }

    private async Task<Network> NetworkAsync(Guid id)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Networks.Include(n => n.FirewallRules).SingleAsync(n => n.Id == id);
    }

    [Fact]
    public async Task EveryWaitingRequest_GetsItsKindFromTheDesiredExistence_AndATime()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedNetworkAsync("Exists", "Deleted", hasExternalId: true, waiting: true);
        Guid creating = await SeedNetworkAsync("NotCreated", "Exists", hasExternalId: false, waiting: true);
        Guid resting = await SeedNetworkAsync("Exists", "Exists", hasExternalId: true, waiting: false);
        Guid ruleDeleting = await SeedRuleAsync(resting, "Exists", "Deleted", hasExternalId: true);
        Guid ruleCreating = await SeedRuleAsync(resting, "NotCreated", "Exists", hasExternalId: false);

        await MigrateToLatestAsync();

        Network delete = await NetworkAsync(deleting);
        Assert.Equal(NetworkOperationKind.Delete, delete.Pending!.Kind);
        Assert.True(delete.Pending.AskedAt > DateTimeOffset.UnixEpoch);
        Assert.Equal(NetworkOperationKind.Create, (await NetworkAsync(creating)).Pending!.Kind);
        Network rest = await NetworkAsync(resting);
        Assert.Null(rest.Pending);
        Assert.Equal(FirewallRuleOperationKind.Delete, rest.FirewallRules.Single(r => r.Id == ruleDeleting).Pending!.Kind);
        Assert.Equal(FirewallRuleOperationKind.Create, rest.FirewallRules.Single(r => r.Id == ruleCreating).Pending!.Kind);
    }

    // A create whose command already reported the provider id waited only for the reconciler's Created, which no longer
    // ends a request (titan-ops architecture/resource-lifecycle.md#invariants): no report is coming, so it ends here. A
    // create without the id, and a delete, still have their command's report coming.
    [Fact]
    public async Task ACreateWithItsProviderId_Ends_ACreateWithoutIt_AndADelete_KeepWaiting()
    {
        await MigrateToAsync(MigrationBefore);
        Guid reported = await SeedNetworkAsync("Exists", "Exists", hasExternalId: true, waiting: true);
        Guid notReported = await SeedNetworkAsync("NotCreated", "Exists", hasExternalId: false, waiting: true);
        Guid deleting = await SeedNetworkAsync("Exists", "Deleted", hasExternalId: true, waiting: true);
        Guid resting = await SeedNetworkAsync("Exists", "Exists", hasExternalId: true, waiting: false);
        Guid ruleReported = await SeedRuleAsync(resting, "Exists", "Exists", hasExternalId: true);
        Guid ruleNotReported = await SeedRuleAsync(resting, "NotCreated", "Exists", hasExternalId: false);
        Guid ruleDeleting = await SeedRuleAsync(resting, "Exists", "Deleted", hasExternalId: true);

        await MigrateToLatestAsync();

        Network ended = await NetworkAsync(reported);
        Assert.Null(ended.Pending);
        Assert.Equal($"net-{reported:N}", ended.ExternalId);
        Assert.Equal(NetworkOperationKind.Create, (await NetworkAsync(notReported)).Pending!.Kind);
        Assert.Equal(NetworkOperationKind.Delete, (await NetworkAsync(deleting)).Pending!.Kind);
        Network rest = await NetworkAsync(resting);
        Assert.Null(rest.FirewallRules.Single(r => r.Id == ruleReported).Pending);
        Assert.Equal(FirewallRuleOperationKind.Create, rest.FirewallRules.Single(r => r.Id == ruleNotReported).Pending!.Kind);
        Assert.Equal(FirewallRuleOperationKind.Delete, rest.FirewallRules.Single(r => r.Id == ruleDeleting).Pending!.Kind);
    }

    [Fact]
    public async Task AnOpenCommand_TakesTheRequestsKeyAndId_ARulesUnderFirewallRule()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedNetworkAsync("Exists", "Deleted", hasExternalId: true, waiting: true);
        await AddOpenCommandAsync($"network-delete-{deleting}", ProviderCommandKinds.NetworkDelete);
        Guid creating = await SeedNetworkAsync("NotCreated", "Exists", hasExternalId: false, waiting: true);
        await AddOpenCommandAsync($"network-create-{creating}", ProviderCommandKinds.NetworkCreate);
        Guid resting = await SeedNetworkAsync("Exists", "Exists", hasExternalId: true, waiting: false);
        Guid ruleDeleting = await SeedRuleAsync(resting, "Exists", "Deleted", hasExternalId: true);
        await AddOpenCommandAsync($"firewall-delete-{ruleDeleting}", ProviderCommandKinds.FirewallRuleDelete);
        Guid ruleCreating = await SeedRuleAsync(resting, "NotCreated", "Exists", hasExternalId: false);
        await AddOpenCommandAsync($"firewall-create-{ruleCreating}", ProviderCommandKinds.FirewallRuleCreate);

        await MigrateToLatestAsync();

        Guid deleteRequest = (await NetworkAsync(deleting)).Pending!.Id;
        Guid createRequest = (await NetworkAsync(creating)).Pending!.Id;
        Network rest = await NetworkAsync(resting);
        Guid ruleDeleteRequest = rest.FirewallRules.Single(r => r.Id == ruleDeleting).Pending!.Id;
        Guid ruleCreateRequest = rest.FirewallRules.Single(r => r.Id == ruleCreating).Pending!.Id;
        await using TitanDbContext read = NewDbContext();
        Dictionary<string, string> payloadByKey = await read.ProviderCommands.ToDictionaryAsync(c => c.Key, c => c.Payload);
        Assert.Equal(4, payloadByKey.Count);
        Assert.Equal(deleteRequest.ToString(), RequestIdIn(payloadByKey[$"network-delete-{deleting}-{deleteRequest}"]));
        Assert.Equal(createRequest.ToString(), RequestIdIn(payloadByKey[$"network-create-{creating}-{createRequest}"]));
        Assert.Equal(ruleDeleteRequest.ToString(),
            RequestIdIn(payloadByKey[$"firewall-rule-delete-{ruleDeleting}-{ruleDeleteRequest}"]));
        Assert.Equal(ruleCreateRequest.ToString(),
            RequestIdIn(payloadByKey[$"firewall-rule-create-{ruleCreating}-{ruleCreateRequest}"]));
    }

    private static string? RequestIdIn(string payload)
    {
        using JsonDocument json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("requestId").GetString();
    }

    [Fact]
    public async Task TheView_ListsWaitingNetworksAndRules_AndANetworkOfTheNewModelInserts()
    {
        await MigrateToLatestAsync();
        Guid tenantId = Guid.NewGuid();
        Network withARule = Network.Create(tenantId, NetworkName.Parse("after"), "cluster-a", null, wantsPublicIpv6: false);
        withARule.FinishOperation(withARule.Pending!.Id, "net-after", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        withARule.AddFirewallRule(Guid.NewGuid(), FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8"));
        Network creating = Network.Create(tenantId, NetworkName.Parse("creating"), "cluster-a", null, wantsPublicIpv6: false);
        await using (TitanDbContext db = NewDbContext())
        {
            db.Networks.AddRange(withARule, creating);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        List<string> rows = await read.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind AS \"Value\" FROM core.pending_requests WHERE tenant_id = {tenantId} ORDER BY resource")
            .ToListAsync();
        Assert.Equal(["firewall-rule|Create", "network|Create"], rows);
    }
}
