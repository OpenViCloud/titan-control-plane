using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with load balancers under the existence pair and the pending kind meets the migration that gives a load
/// balancer one status (titan-ops features/load-balancer/design.md#lb-lifecycle): each combination of the old columns
/// becomes the status the migration's CASE names, in its order, and observed_state keeps Available only while an
/// operation waits on a load balancer Incus was seen holding. The two unique indexes that filtered on current_existence
/// are made again with the same names, filtering on status; the view of waiting requests reads a load balancer's kind off
/// its status; and Down puts the old columns, indexes and view back. The old column names below are the schema before it,
/// by necessity. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class LoadBalancerSingleStatusMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261108105433_VmTemplateClusterSingleStatus";
    private const string ThisMigration = "20261108145755_LoadBalancerSingleStatus";
    private const string Subnet = "2001:db8:0:8::/64";
    private const string NameIndex = "IX_load_balancers_tenant_id_name";

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

    // A load balancer row as the image before this migration wrote it. A waiting operation has its id and kind; one that
    // never reached Incus has no VIP. Each on its own tenant and network, so no unique index clashes.
    private async Task<Guid> SeedOldAsync(string currentExistence, string desiredExistence, string? kind = null,
        bool hasVip = true, string? reason = null, bool kindWithoutAnId = false)
    {
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (kind != null && !kindWithoutAnId)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? vip = hasVip ? "2001:db8:0:8::1:1" : null;
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.load_balancers (id, tenant_id, network_id, external_network_id, network_subnet, cluster_id, name, vip_index,
                                 vip, external_id, current_existence, desired_existence, pending_request_id,
                                 pending_request_kind, pending_request_asked_at, request_failure_reason, created_at)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'net-x', {Subnet}, 'cluster-a', {"lb-" + id.ToString("N")[..8]}, 1,
        '2001:db8:0:8::1:1', {vip}, {currentExistence}, {desiredExistence}, {requestId}, {kind}, {askedAt}, {reason}, now())");
        return id;
    }

    // A load balancer row as the model of today writes it.
    private async Task<Guid> SeedNewAsync(string status, Guid? tenantId = null, Guid? networkId = null, int vipIndex = 1,
        string? name = null)
    {
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (status is "Creating" or "Updating" or "Deleting")
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? vip = status == "Creating" || status == "Failed" ? null : $"2001:db8:0:8::1:{vipIndex}";
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.load_balancers (id, tenant_id, network_id, external_network_id, network_subnet, cluster_id, name, vip_index,
                                 vip, external_id, status, pending_request_id, pending_request_asked_at, created_at)
VALUES ({id}, {tenantId ?? Guid.NewGuid()}, {networkId ?? Guid.NewGuid()}, 'net-x', {Subnet}, 'cluster-a',
        {name ?? "lb-" + id.ToString("N")[..8]}, {vipIndex}, {$"2001:db8:0:8::1:{vipIndex}"}, {vip}, {status}, {requestId}, {askedAt}, now())");
        return id;
    }

    // Each load balancer's status and observed_state after the migration, read in SQL: "-" stands for null.
    private async Task<Dictionary<Guid, string>> StatusAndObservedStateAsync()
    {
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.load_balancers")
            .ToListAsync();
        return rows.ToDictionary(row => Guid.Parse(row[..36]), row => row[37..]);
    }

    [Fact]
    public async Task EveryOldCombination_BecomesTheStatusTheCaseNames_AndObservedStateIsAvailableOnlyWhileAnOperationWaitsOnALoadBalancerIncusHeld()
    {
        await MigrateToAsync(MigrationBefore);
        // One row per branch of the CASE, in its order.
        Guid deleted = await SeedOldAsync("Deleted", "Deleted");
        Guid deletedBeforeIncus = await SeedOldAsync("Deleted", "Deleted", hasVip: false, reason: "creation failed");
        Guid deletingSeen = await SeedOldAsync("Exists", "Deleted", kind: "Delete");
        Guid deletingLost = await SeedOldAsync("NotFound", "Deleted", kind: "Delete");
        Guid updatingSeen = await SeedOldAsync("Exists", "Exists", kind: "Update");
        Guid updatingLost = await SeedOldAsync("NotFound", "Exists", kind: "Update");
        Guid creating = await SeedOldAsync("NotCreated", "Exists", kind: "Create", hasVip: false);
        // Reached only through the observation endpoint: it wrote over the row of a create that waits.
        Guid creatingObservedHere = await SeedOldAsync("Exists", "Exists", kind: "Create", hasVip: false);
        Guid creatingObservedGone = await SeedOldAsync("NotFound", "Exists", kind: "Create", hasVip: false);
        Guid gaveUp = await SeedOldAsync("NotCreated", "Exists", hasVip: false, reason: "creation failed");
        Guid gaveUpObservedHere = await SeedOldAsync("Exists", "Exists", hasVip: false, reason: "creation failed");
        Guid available = await SeedOldAsync("Exists", "Exists");
        Guid availableAfterAGiveUp = await SeedOldAsync("Exists", "Exists", reason: "update failed");
        Guid notFound = await SeedOldAsync("NotFound", "Exists");
        // Combinations the CASE does not name end in its ELSE: Available.
        Guid aKindWithoutAnId = await SeedOldAsync("Exists", "Exists", kind: "Update", kindWithoutAnId: true);
        Guid aVipOnARowNeverCreated = await SeedOldAsync("NotCreated", "Exists");

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byId = await StatusAndObservedStateAsync();
        Assert.Equal("Deleted|-", byId[deleted]);
        Assert.Equal("Deleted|-", byId[deletedBeforeIncus]);
        Assert.Equal("Deleting|Available", byId[deletingSeen]);
        Assert.Equal("Deleting|-", byId[deletingLost]);
        Assert.Equal("Updating|Available", byId[updatingSeen]);
        Assert.Equal("Updating|-", byId[updatingLost]);
        Assert.Equal("Creating|-", byId[creating]);
        Assert.Equal("Creating|Available", byId[creatingObservedHere]);
        Assert.Equal("Creating|-", byId[creatingObservedGone]);
        Assert.Equal("Failed|-", byId[gaveUp]);
        Assert.Equal("Failed|-", byId[gaveUpObservedHere]);
        Assert.Equal("Available|-", byId[available]);
        Assert.Equal("Available|-", byId[availableAfterAGiveUp]);
        Assert.Equal("NotFound|-", byId[notFound]);
        Assert.Equal("Available|-", byId[aKindWithoutAnId]);
        Assert.Equal("Available|-", byId[aVipOnARowNeverCreated]);
    }

    // What the domain reads off the migrated row: the status, the operation kept, the reason, and what the quota counts.
    [Fact]
    public async Task AMigratedLoadBalancer_ReadsBackItsStatus_ItsOperation_ItsReason_AndWhatTheQuotaCounts()
    {
        await MigrateToAsync(MigrationBefore);
        Guid updatingSeen = await SeedOldAsync("Exists", "Exists", kind: "Update", reason: "earlier update failed");
        Guid updatingLost = await SeedOldAsync("NotFound", "Exists", kind: "Update");
        Guid deletingSeen = await SeedOldAsync("Exists", "Deleted", kind: "Delete");
        Guid notFound = await SeedOldAsync("NotFound", "Exists", reason: "update failed");
        Guid failed = await SeedOldAsync("NotCreated", "Exists", hasVip: false, reason: "creation failed");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        LoadBalancer Load(Guid id) => db.LoadBalancers.Single(lb => lb.Id == id);
        LoadBalancer seen = Load(updatingSeen);
        Assert.Equal((LoadBalancerStatus.Updating, (LoadBalancerStatus?)LoadBalancerStatus.Available, true),
            (seen.Status, seen.InstanceStatus, seen.OccupiesQuota));
        Assert.NotNull(seen.Operation.PendingId);
        Assert.NotNull(seen.Operation.AskedAt);
        Assert.Equal("earlier update failed", seen.Operation.FailureReason);
        LoadBalancer lost = Load(updatingLost);
        Assert.Equal((LoadBalancerStatus.Updating, (LoadBalancerStatus?)null, false), (lost.Status, lost.InstanceStatus, lost.OccupiesQuota));
        LoadBalancer deleting = Load(deletingSeen);
        Assert.Equal((LoadBalancerStatus.Deleting, (LoadBalancerStatus?)LoadBalancerStatus.Available, false),
            (deleting.Status, deleting.InstanceStatus, deleting.OccupiesQuota));
        LoadBalancer gone = Load(notFound);
        Assert.Equal((LoadBalancerStatus.NotFound, (LoadBalancerStatus?)null, false), (gone.Status, gone.InstanceStatus, gone.OccupiesQuota));
        Assert.Null(gone.Operation.PendingId);
        Assert.Equal("update failed", gone.Operation.FailureReason);
        LoadBalancer gaveUp = Load(failed);
        Assert.Equal((LoadBalancerStatus.Failed, false), (gaveUp.Status, gaveUp.OccupiesQuota));
        Assert.Equal("creation failed", gaveUp.Operation.FailureReason);
    }

    [Fact]
    public async Task TheOldColumnsAreGone_AndTheStatusAndObservedStateAreThere()
    {
        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> columns = await db.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'load_balancers'")
            .ToListAsync();
        Assert.DoesNotContain("current_existence", columns);
        Assert.DoesNotContain("desired_existence", columns);
        Assert.DoesNotContain("pending_request_kind", columns);
        Assert.Contains("status", columns);
        Assert.Contains("observed_state", columns);
        Assert.Contains("pending_request_id", columns);
        Assert.Contains("pending_request_asked_at", columns);
        Assert.Contains("request_failure_reason", columns);
        Assert.Contains("request_failure_detail", columns);
        Assert.Contains("request_failed_at", columns);
    }

    // The old indexes filtered on current_existence, which is gone: the two that exist now carry the same names, are unique
    // on the same columns, and filter on status.
    [Fact]
    public async Task TheTwoUniqueIndexes_KeepTheirNamesAndColumns_AndFilterOnStatus()
    {
        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> definitions = await db.Database
            .SqlQueryRaw<string>("SELECT indexname || '|' || indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'core' AND tablename = 'load_balancers' AND indexname IN ('IX_load_balancers_tenant_id_name', 'ix_load_balancers_network_id_vip_index')")
            .ToListAsync();
        Assert.Equal(2, definitions.Count);
        string name = Assert.Single(definitions, d => d.StartsWith(NameIndex + "|", StringComparison.Ordinal));
        Assert.Contains("UNIQUE", name);
        Assert.Contains("(tenant_id, name)", name);
        string vip = Assert.Single(definitions, d => d.StartsWith(LoadBalancerConfiguration.VipIndexName + "|", StringComparison.Ordinal));
        Assert.Contains("UNIQUE", vip);
        Assert.Contains("(network_id, vip_index)", vip);
        Assert.All(definitions, d =>
        {
            Assert.Contains("status", d);
            Assert.DoesNotContain("current_existence", d);
        });
    }

    // Behaviour, not text: a live duplicate is refused on either index, and a Deleted load balancer gives its name and its
    // number back.
    [Fact]
    public async Task TheIndexes_RefuseALiveDuplicateNameOrVip_ButNotADeletedOne()
    {
        await MigrateToAsync(ThisMigration);
        Guid tenant = Guid.NewGuid();
        Guid network = Guid.NewGuid();
        await SeedNewAsync("Available", tenant, network, vipIndex: 1, name: "web");

        var sameName = await Assert.ThrowsAsync<PostgresException>(() => SeedNewAsync("Creating", tenant, Guid.NewGuid(), vipIndex: 1, name: "web"));
        var sameVip = await Assert.ThrowsAsync<PostgresException>(() => SeedNewAsync("Creating", Guid.NewGuid(), network, vipIndex: 1, name: "other"));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, NameIndex), (sameName.SqlState, sameName.ConstraintName));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, LoadBalancerConfiguration.VipIndexName), (sameVip.SqlState, sameVip.ConstraintName));
        // Failed, NotFound, Updating and Deleting all still hold the name and the number.
        foreach (string status in new[] { "Failed", "NotFound", "Updating", "Deleting" })
        {
            Guid t = Guid.NewGuid();
            Guid n = Guid.NewGuid();
            await SeedNewAsync(status, t, n, vipIndex: 1, name: "held");
            await Assert.ThrowsAsync<PostgresException>(() => SeedNewAsync("Creating", t, Guid.NewGuid(), vipIndex: 1, name: "held"));
            await Assert.ThrowsAsync<PostgresException>(() => SeedNewAsync("Creating", Guid.NewGuid(), n, vipIndex: 1, name: "elsewhere"));
        }
        // Deleted gives both back, and many Deleted rows may share them.
        Guid deletedTenant = Guid.NewGuid();
        Guid deletedNetwork = Guid.NewGuid();
        await SeedNewAsync("Deleted", deletedTenant, deletedNetwork, vipIndex: 1, name: "gone");
        await SeedNewAsync("Deleted", deletedTenant, deletedNetwork, vipIndex: 1, name: "gone");
        await SeedNewAsync("Available", deletedTenant, deletedNetwork, vipIndex: 1, name: "gone");
    }

    // The view of waiting requests names a load balancer's kind from its status, the word PendingRequestReader lowercases into
    // the command key: a wrong word here lists nothing as stuck and reads green.
    [Fact]
    public async Task TheViewOfWaitingRequests_NamesALoadBalancersKind_FromItsStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid creating = await SeedOldAsync("NotCreated", "Exists", kind: "Create", hasVip: false);
        Guid updating = await SeedOldAsync("Exists", "Exists", kind: "Update");
        Guid deleting = await SeedOldAsync("NotFound", "Deleted", kind: "Delete");
        Guid resting = await SeedOldAsync("Exists", "Exists");
        Guid gaveUp = await SeedOldAsync("NotCreated", "Exists", hasVip: false, reason: "x");

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> kinds = await WaitingKindsAsync();
        Assert.Equal("Create", kinds[creating]);
        Assert.Equal("Update", kinds[updating]);
        Assert.Equal("Delete", kinds[deleting]);
        Assert.DoesNotContain(resting, kinds.Keys);
        Assert.DoesNotContain(gaveUp, kinds.Keys);
    }

    private async Task<Dictionary<Guid, string>> WaitingKindsAsync()
    {
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'load-balancer'")
            .ToListAsync();
        return rows.ToDictionary(row => Guid.Parse(row[..36]), row => row[37..]);
    }

    // A load balancer written by the model of today stores its status and what it was seen holding by name, and the
    // view lists it with the request id the command key ends with.
    [Fact]
    public async Task ALoadBalancerWrittenNow_StoresItsStatusAndObservedStateByName_AndTheViewListsIt()
    {
        await MigrateToAsync(ThisMigration);
        Guid tenantId = Guid.NewGuid();
        Guid networkId = Guid.NewGuid();
        Vm backend = Vm.Create(tenantId, networkId, "vm-a", "m", "tpl", "cluster-a", 20);
        backend.MarkRunning("vm-a", "10.0.0.5");
        LoadBalancer lb = LoadBalancer.Create(tenantId, "web", networkId, "net-x", Subnet, "cluster-a",
            LoadBalancerVip.For(Subnet, 1), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [backend]);
        lb.CreateSucceeded(lb.Operation.PendingId!.Value, lb.Vip);
        Guid update = lb.Update([new LoadBalancerListenerSpec(LoadBalancerProtocol.UDP, 53, 53)], [backend]).OperationId;
        await using (TitanDbContext db = NewDbContext())
        {
            db.LoadBalancers.Add(lb);
            await db.SaveChangesAsync();
        }

        Dictionary<Guid, string> byId = await StatusAndObservedStateAsync();
        Assert.Equal("Updating|Available", byId[lb.Id]);
        await using TitanDbContext read = NewDbContext();
        List<string> waiting = await read.Database
            .SqlQuery<string>($"SELECT kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE resource = 'load-balancer' AND id = {lb.Id}")
            .ToListAsync();
        Assert.Equal([$"Update|{update}"], waiting);
    }

    [Fact]
    public async Task Down_PutsTheOldColumnsIndexesAndViewBack_FromTheStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid creating = await SeedOldAsync("NotCreated", "Exists", kind: "Create", hasVip: false);
        Guid updatingSeen = await SeedOldAsync("Exists", "Exists", kind: "Update");
        Guid updatingLost = await SeedOldAsync("NotFound", "Exists", kind: "Update");
        Guid deletingSeen = await SeedOldAsync("Exists", "Deleted", kind: "Delete");
        Guid deletingLost = await SeedOldAsync("NotFound", "Deleted", kind: "Delete");
        Guid gaveUp = await SeedOldAsync("NotCreated", "Exists", hasVip: false, reason: "x");
        Guid available = await SeedOldAsync("Exists", "Exists");
        Guid notFound = await SeedOldAsync("NotFound", "Exists");
        Guid deleted = await SeedOldAsync("Deleted", "Deleted");
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database.SqlQueryRaw<string>(
            "SELECT id::text || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') AS \"Value\" FROM core.load_balancers")
            .ToListAsync();
        Assert.Contains($"{creating}|NotCreated|Exists|Create", rows);
        Assert.Contains($"{updatingSeen}|Exists|Exists|Update", rows);
        Assert.Contains($"{updatingLost}|NotFound|Exists|Update", rows);
        Assert.Contains($"{deletingSeen}|Exists|Deleted|Delete", rows);
        Assert.Contains($"{deletingLost}|NotFound|Deleted|Delete", rows);
        Assert.Contains($"{gaveUp}|NotCreated|Exists|-", rows);
        Assert.Contains($"{available}|Exists|Exists|-", rows);
        Assert.Contains($"{notFound}|NotFound|Exists|-", rows);
        Assert.Contains($"{deleted}|Deleted|Deleted|-", rows);
        List<string> columns = await db.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'load_balancers'")
            .ToListAsync();
        Assert.DoesNotContain("status", columns);
        Assert.DoesNotContain("observed_state", columns);
        List<string> definitions = await db.Database
            .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'core' AND tablename = 'load_balancers' AND indexname IN ('IX_load_balancers_tenant_id_name', 'ix_load_balancers_network_id_vip_index')")
            .ToListAsync();
        Assert.Equal(2, definitions.Count);
        Assert.All(definitions, d => Assert.Contains("current_existence", d));
        Dictionary<Guid, string> kinds = await WaitingKindsAsync();
        Assert.Equal(
            new Dictionary<Guid, string>
            {
                [creating] = "Create", [updatingSeen] = "Update", [updatingLost] = "Update",
                [deletingSeen] = "Delete", [deletingLost] = "Delete",
            },
            kinds);
    }
}
