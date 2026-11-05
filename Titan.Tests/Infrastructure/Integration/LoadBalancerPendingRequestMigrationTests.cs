using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with load balancers waiting on a request under the old column meets the migration that gives each request
/// its kind and time (titan-ops architecture/resource-lifecycle.md#shape): the kind is read in the order
/// LoadBalancer.Transition read it (desired Deleted, then current NotCreated, else an update), an open create or delete
/// takes the request's key and id, an open update takes the id its key already ends with, and the view lists the table.
/// The old column name and keys below are the schema before it, by necessity. Category=Integration — its own CI step;
/// needs real Postgres.
[Trait("Category", "Integration")]
public sealed class LoadBalancerPendingRequestMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261103112316_VmSnapshotPendingRequest";
    private const string Subnet = "2001:db8:0:7::/64";

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

    // A load balancer row as the image before this migration wrote it: the request's id in pending_intent_id, its kind
    // implied by the two existences. Each on its own network, so the VIP index never clashes.
    private async Task<(Guid Id, Guid? Intent)> SeedLoadBalancerAsync(string currentExistence, string desiredExistence, bool waiting)
    {
        Guid id = Guid.NewGuid();
        Guid? intent = waiting ? Guid.NewGuid() : null;
        string? externalId = currentExistence == "NotCreated" ? null : "2001:db8:0:7::1:1";
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.load_balancers (id, tenant_id, network_id, external_network_id, network_subnet, cluster_id, name, vip_index,
                                 vip, external_id, current_existence, desired_existence, pending_intent_id, created_at)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'net-x', {Subnet}, 'cluster-a', {"lb-" + id.ToString("N")[..8]}, 1,
        '2001:db8:0:7::1:1', {externalId}, {currentExistence}, {desiredExistence}, {intent}, now())");
        return (id, intent);
    }

    private async Task AddCommandAsync(string key, string kind, string payload, bool done = false)
    {
        await using TitanDbContext seed = NewDbContext();
        await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, key, kind, "cluster-a", payload, done ? "Done" : "Pending");
    }

    private async Task<LoadBalancer> LoadBalancerAsync(Guid id)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.LoadBalancers.Include(lb => lb.Listeners).Include(lb => lb.Backends).SingleAsync(lb => lb.Id == id);
    }

    [Fact]
    public async Task EveryWaitingRequest_GetsItsKindInTheOrderTransitionReadIt_AndATime()
    {
        await MigrateToAsync(MigrationBefore);
        var deleting = await SeedLoadBalancerAsync("Exists", "Deleted", waiting: true);
        var deletingLost = await SeedLoadBalancerAsync("NotFound", "Deleted", waiting: true);
        var creating = await SeedLoadBalancerAsync("NotCreated", "Exists", waiting: true);
        var updating = await SeedLoadBalancerAsync("Exists", "Exists", waiting: true);
        var updatingLost = await SeedLoadBalancerAsync("NotFound", "Exists", waiting: true);
        var resting = await SeedLoadBalancerAsync("Exists", "Exists", waiting: false);

        await MigrateToLatestAsync();

        LoadBalancer delete = await LoadBalancerAsync(deleting.Id);
        Assert.Equal((LoadBalancerOperationKind.Delete, deleting.Intent!.Value), (delete.Pending!.Kind, delete.Pending.Id));
        Assert.True(delete.Pending.AskedAt > DateTimeOffset.UnixEpoch);
        Assert.Equal(LoadBalancerOperationKind.Delete, (await LoadBalancerAsync(deletingLost.Id)).Pending!.Kind);
        Assert.Equal(LoadBalancerOperationKind.Create, (await LoadBalancerAsync(creating.Id)).Pending!.Kind);
        Assert.Equal(LoadBalancerOperationKind.Update, (await LoadBalancerAsync(updating.Id)).Pending!.Kind);
        Assert.Equal(LoadBalancerOperationKind.Update, (await LoadBalancerAsync(updatingLost.Id)).Pending!.Kind);
        Assert.Null((await LoadBalancerAsync(resting.Id)).Pending);
    }

    [Fact]
    public async Task AnOpenCommand_TakesTheRequestsKeyAndId_AnUpdateKeepsItsKey_AFinishedOneIsLeftAlone()
    {
        await MigrateToAsync(MigrationBefore);
        var creating = await SeedLoadBalancerAsync("NotCreated", "Exists", waiting: true);
        await AddCommandAsync($"load-balancer-create-{creating.Id}", ProviderCommandKinds.LoadBalancerCreate,
            $$"""{"loadBalancerId":"{{creating.Id}}","intentId":null}""");
        var updating = await SeedLoadBalancerAsync("Exists", "Exists", waiting: true);
        string updateKey = $"load-balancer-update-{updating.Id}-{updating.Intent}";
        await AddCommandAsync(updateKey, ProviderCommandKinds.LoadBalancerUpdate,
            $$"""{"loadBalancerId":"{{updating.Id}}","intentId":"{{updating.Intent}}"}""");
        var deleting = await SeedLoadBalancerAsync("Exists", "Deleted", waiting: true);
        await AddCommandAsync($"load-balancer-delete-{deleting.Id}", ProviderCommandKinds.LoadBalancerDelete,
            $$"""{"loadBalancerId":"{{deleting.Id}}"}""");
        // The update the delete replaced, still open: it takes its own id, which no longer names the waiting request.
        Guid replaced = Guid.NewGuid();
        string replacedKey = $"load-balancer-update-{deleting.Id}-{replaced}";
        await AddCommandAsync(replacedKey, ProviderCommandKinds.LoadBalancerUpdate,
            $$"""{"loadBalancerId":"{{deleting.Id}}","intentId":"{{replaced}}"}""");
        // A finished row under the key of the waiting request (its report was lost) is history: the migration leaves it
        // keyed as it was.
        var deletingReportLost = await SeedLoadBalancerAsync("Exists", "Deleted", waiting: true);
        string finishedKey = $"load-balancer-delete-{deletingReportLost.Id}";
        await AddCommandAsync(finishedKey, ProviderCommandKinds.LoadBalancerDelete, "{}", done: true);

        await MigrateToLatestAsync();

        await using TitanDbContext read = NewDbContext();
        Dictionary<string, string> payloadByKey = await read.ProviderCommands.ToDictionaryAsync(c => c.Key, c => c.Payload);
        Assert.Equal(5, payloadByKey.Count);
        Assert.Equal(creating.Intent.ToString(), RequestIdIn(payloadByKey[$"load-balancer-create-{creating.Id}-{creating.Intent}"]));
        Assert.Equal(updating.Intent.ToString(), RequestIdIn(payloadByKey[updateKey]));
        Assert.Equal(deleting.Intent.ToString(), RequestIdIn(payloadByKey[$"load-balancer-delete-{deleting.Id}-{deleting.Intent}"]));
        Assert.Equal(replaced.ToString(), RequestIdIn(payloadByKey[replacedKey]));
        Assert.Equal("{}", payloadByKey[finishedKey]);
        // The rest of the payload is kept.
        using JsonDocument update = JsonDocument.Parse(payloadByKey[updateKey]);
        Assert.Equal(updating.Intent.ToString(), update.RootElement.GetProperty("intentId").GetString());
    }

    private static string? RequestIdIn(string payload)
    {
        using JsonDocument json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("requestId").GetString();
    }

    [Fact]
    public async Task TheView_ListsWaitingLoadBalancers_AndALoadBalancerOfTheNewModelInserts()
    {
        await MigrateToLatestAsync();
        Guid tenantId = Guid.NewGuid();
        Guid networkId = Guid.NewGuid();
        Vm backend = Vm.Create(tenantId, networkId, "vm-after", "m", "tpl", "cluster-a", 20);
        backend.FinishOperation(backend.Pending!.Id, "vm-after", VmState.Running, "10.0.0.5", null);
        LoadBalancerListenerSpec http = new(LoadBalancerProtocol.TCP, 80, 8080);
        LoadBalancer creating = LoadBalancer.Create(tenantId, "creating", networkId, "net-x", Subnet, "cluster-a",
            LoadBalancerVip.For(Subnet, 1), [http], [backend]);
        LoadBalancer updating = LoadBalancer.Create(tenantId, "updating", networkId, "net-x", Subnet, "cluster-a",
            LoadBalancerVip.For(Subnet, 2), [http], [backend]);
        updating.FinishOperation(updating.Pending!.Id, updating.Vip);
        OperationTicket update = updating.Update([new(LoadBalancerProtocol.UDP, 53, 53)], [backend]);
        await using (TitanDbContext db = NewDbContext())
        {
            db.LoadBalancers.AddRange(creating, updating);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        List<string> rows = await read.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE tenant_id = {tenantId} ORDER BY kind")
            .ToListAsync();
        Assert.Equal([$"load-balancer|Create|{creating.Pending!.Id}", $"load-balancer|Update|{update.OperationId}"], rows);
    }

    // Down puts the view of the earlier phases back before the kind column goes, and the old column name.
    [Fact]
    public async Task Down_RestoresTheColumnAndTheEarlierView()
    {
        await MigrateToLatestAsync();
        await MigrateToAsync(MigrationBefore);

        var waiting = await SeedLoadBalancerAsync("NotCreated", "Exists", waiting: true);
        await using TitanDbContext read = NewDbContext();
        int listed = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM core.pending_requests WHERE id = {waiting.Id}")
            .SingleAsync();
        Assert.Equal(0, listed);
    }
}
