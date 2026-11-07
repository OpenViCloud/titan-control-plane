using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with networks and firewall rules under the desired and existence pairs and the pending kind meets the
/// migration that gives each one status (titan-ops features/network/design.md#network-data): each combination of the old
/// columns becomes the status the migration's CASE names, in its order, and observed_state keeps the Incus word only while
/// a delete waits on a network or rule Incus still shows. The old column names below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class NetworkSingleStatusMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261105105217_VmSingleStatus";
    private const string ThisMigration = "20261107123233_NetworkSingleStatus";

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

    // A network row as the image before this migration wrote it. A waiting operation has its id and kind; a network that
    // never reached Incus has no provider id.
    private async Task<Guid> SeedNetworkAsync(string currentExistence, string? currentState, string? pendingKind = null,
        bool namedOnIncus = true)
    {
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (pendingKind != null)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? externalId = null;
        if (namedOnIncus)
        {
            externalId = "net-" + id.ToString("N")[..12];
        }
        string desiredExistence = "Exists";
        if (currentExistence == "Deleted" || pendingKind == "Delete")
        {
            desiredExistence = "Deleted";
        }
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.networks (id, tenant_id, name, cluster_id, current_state, current_existence, desired_existence, external_id,
                           created_at, pending_request_id, pending_request_kind, pending_request_asked_at)
VALUES ({id}, {Guid.NewGuid()}, {"net-" + id.ToString("N")[..8]}, 'cluster-a', {currentState}, {currentExistence}, {desiredExistence},
        {externalId}, now(), {requestId}, {pendingKind}, {askedAt})");
        return id;
    }

    // A rule row as the image before this migration wrote it, under a network of its own.
    private async Task<Guid> SeedRuleAsync(string currentExistence, string? currentState, string? pendingKind = null,
        bool namedOnIncus = true)
    {
        Guid networkId = await SeedNetworkAsync("Exists", "Created");
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (pendingKind != null)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? externalId = null;
        if (namedOnIncus)
        {
            externalId = "acl-" + id.ToString("N")[..12];
        }
        string desiredExistence = "Exists";
        if (currentExistence == "Deleted" || pendingKind == "Delete")
        {
            desiredExistence = "Deleted";
        }
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.firewall_rules (id, network_id, tenant_id, direction, protocol, cidr, current_state, current_existence,
                                 desired_existence, external_id, created_at, pending_request_id, pending_request_kind,
                                 pending_request_asked_at)
VALUES ({id}, {networkId}, {Guid.NewGuid()}, 'Ingress', 'TCP', '10.0.0.0/8', {currentState}, {currentExistence},
        {desiredExistence}, {externalId}, now(), {requestId}, {pendingKind}, {askedAt})");
        return id;
    }

    // Each row's status and observed_state after the migration, read in SQL: "-" stands for a null word.
    private const string NetworkStatusAndWordSql =
        "SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.networks";
    private const string RuleStatusAndWordSql =
        "SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.firewall_rules";

    private async Task<Dictionary<Guid, string>> StatusAndWordAsync(string table)
    {
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>(table == "networks" ? NetworkStatusAndWordSql : RuleStatusAndWordSql)
            .ToListAsync();
        Dictionary<Guid, string> byId = new();
        foreach (string row in rows)
        {
            int cut = row.IndexOf('|');
            byId[Guid.Parse(row[..cut])] = row[(cut + 1)..];
        }
        return byId;
    }

    [Fact]
    public async Task EveryOldCombinationOfANetwork_BecomesTheStatusTheCaseNames_AndObservedStateKeepsTheWordOnlyWhileADeleteWaits()
    {
        await MigrateToAsync(MigrationBefore);
        // One row per branch of the CASE, in its order.
        Guid deleted = await SeedNetworkAsync("Deleted", null);
        Guid creating = await SeedNetworkAsync("NotCreated", null, pendingKind: "Create", namedOnIncus: false);
        Guid deletingCreated = await SeedNetworkAsync("Exists", "Created", pendingKind: "Delete");
        Guid deletingWithoutAWord = await SeedNetworkAsync("Exists", null, pendingKind: "Delete");
        Guid deletingNotFound = await SeedNetworkAsync("NotFound", null, pendingKind: "Delete");
        Guid failed = await SeedNetworkAsync("NotCreated", null, namedOnIncus: false);
        // A create that gave up with a word and no id: the old model counted it, the status says it never reached Incus.
        Guid existsWithoutAnId = await SeedNetworkAsync("Exists", "Errored", namedOnIncus: false);
        Guid notFound = await SeedNetworkAsync("NotFound", null);
        Guid created = await SeedNetworkAsync("Exists", "Created");
        Guid errored = await SeedNetworkAsync("Exists", "Errored");
        Guid pending = await SeedNetworkAsync("Exists", "Pending");
        Guid unavailable = await SeedNetworkAsync("Exists", "Unavailable");
        Guid unknownWord = await SeedNetworkAsync("Exists", "Unknown");
        // The order of the CASE: a waiting operation names the move before what the reconciler last saw.
        Guid deleteWaitingNotFoundWithAWord = await SeedNetworkAsync("NotFound", "Created", pendingKind: "Delete");
        // A combination the CASE does not name: with a provider id the reconciler writes the real status on its next cycle.
        Guid existsWithoutAWord = await SeedNetworkAsync("Exists", null);
        Guid notCreatedWithAnId = await SeedNetworkAsync("NotCreated", null);

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byId = await StatusAndWordAsync("networks");
        Assert.Equal("Deleted|-", byId[deleted]);
        Assert.Equal("Creating|-", byId[creating]);
        Assert.Equal("Deleting|Created", byId[deletingCreated]);
        Assert.Equal("Deleting|Unknown", byId[deletingWithoutAWord]);
        Assert.Equal("Deleting|-", byId[deletingNotFound]);
        Assert.Equal("Failed|-", byId[failed]);
        Assert.Equal("Failed|-", byId[existsWithoutAnId]);
        Assert.Equal("NotFound|-", byId[notFound]);
        Assert.Equal("Created|-", byId[created]);
        Assert.Equal("Errored|-", byId[errored]);
        Assert.Equal("Pending|-", byId[pending]);
        Assert.Equal("Unavailable|-", byId[unavailable]);
        Assert.Equal("Unknown|-", byId[unknownWord]);
        Assert.Equal("Deleting|-", byId[deleteWaitingNotFoundWithAWord]);
        Assert.Equal("Unknown|-", byId[existsWithoutAWord]);
        Assert.Equal("Unknown|-", byId[notCreatedWithAnId]);
    }

    [Fact]
    public async Task EveryOldCombinationOfARule_BecomesTheStatusTheCaseNames()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleted = await SeedRuleAsync("Deleted", null);
        Guid creating = await SeedRuleAsync("NotCreated", null, pendingKind: "Create", namedOnIncus: false);
        Guid deletingEnabled = await SeedRuleAsync("Exists", "enabled", pendingKind: "Delete");
        Guid deletingWithoutAState = await SeedRuleAsync("Exists", null, pendingKind: "Delete");
        Guid failed = await SeedRuleAsync("NotCreated", null, namedOnIncus: false);
        Guid existsWithoutAnId = await SeedRuleAsync("Exists", "disabled", namedOnIncus: false);
        Guid notFound = await SeedRuleAsync("NotFound", null);
        Guid enabled = await SeedRuleAsync("Exists", "enabled");
        Guid disabled = await SeedRuleAsync("Exists", "disabled");
        Guid logged = await SeedRuleAsync("Exists", "logged");
        Guid existsWithoutAState = await SeedRuleAsync("Exists", null);

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byId = await StatusAndWordAsync("firewall_rules");
        Assert.Equal("Deleted|-", byId[deleted]);
        Assert.Equal("Creating|-", byId[creating]);
        Assert.Equal("Deleting|enabled", byId[deletingEnabled]);
        Assert.Equal("Deleting|-", byId[deletingWithoutAState]);
        Assert.Equal("Failed|-", byId[failed]);
        Assert.Equal("Failed|-", byId[existsWithoutAnId]);
        Assert.Equal("NotFound|-", byId[notFound]);
        Assert.Equal("enabled|-", byId[enabled]);
        Assert.Equal("disabled|-", byId[disabled]);
        Assert.Equal("logged|-", byId[logged]);
        Assert.Equal("Unknown|-", byId[existsWithoutAState]);
    }

    // The view of waiting requests reads a network's and a rule's kind off the status.
    [Fact]
    public async Task TheViewOfWaitingRequests_NamesTheKindOfANetworkAndARule_FromTheirStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid creating = await SeedNetworkAsync("NotCreated", null, pendingKind: "Create", namedOnIncus: false);
        Guid deleting = await SeedNetworkAsync("Exists", "Created", pendingKind: "Delete");
        Guid ruleDeleting = await SeedRuleAsync("Exists", "enabled", pendingKind: "Delete");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT resource || '|' || id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource IN ('network', 'firewall-rule')")
            .ToListAsync();
        Assert.Contains($"network|{creating}|Create", rows);
        Assert.Contains($"network|{deleting}|Delete", rows);
        Assert.Contains($"firewall-rule|{ruleDeleting}|Delete", rows);
    }

    [Fact]
    public async Task TheNameIndexAndTheView_FollowTheColumns_UpAndDown()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedNetworkAsync("Exists", "Created", pendingKind: "Delete");
        await MigrateToAsync(ThisMigration);

        Assert.Contains("status <> 'Deleted'", await IndexDefinitionAsync());

        await MigrateToAsync(MigrationBefore);

        Assert.Contains("current_existence <> 'Deleted'", await IndexDefinitionAsync());
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT resource || '|' || id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'network'")
            .ToListAsync();
        Assert.Contains($"network|{deleting}|Delete", rows);
    }

    private async Task<string> IndexDefinitionAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database
            .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'core' AND indexname = 'IX_networks_tenant_id_name'")
            .SingleAsync();
    }

    [Fact]
    public async Task Down_PutsTheOldColumnsBackFromTheStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedNetworkAsync("Exists", "Created", pendingKind: "Delete");
        Guid deletingNotFound = await SeedNetworkAsync("NotFound", null, pendingKind: "Delete");
        Guid failed = await SeedNetworkAsync("NotCreated", null, namedOnIncus: false);
        Guid created = await SeedNetworkAsync("Exists", "Errored");
        Guid ruleDeleting = await SeedRuleAsync("Exists", "enabled", pendingKind: "Delete");
        Guid ruleCreating = await SeedRuleAsync("NotCreated", null, pendingKind: "Create", namedOnIncus: false);
        Guid ruleLogged = await SeedRuleAsync("Exists", "logged");
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database.SqlQueryRaw<string>(
            "SELECT id::text || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') || '|' || coalesce(current_state, '-') AS \"Value\" FROM core.networks")
            .ToListAsync();
        Assert.Contains($"{deleting}|Exists|Deleted|Delete|Created", rows);
        Assert.Contains($"{deletingNotFound}|NotFound|Deleted|Delete|-", rows);
        Assert.Contains($"{failed}|NotCreated|Exists|-|-", rows);
        Assert.Contains($"{created}|Exists|Exists|-|Errored", rows);
        List<string> ruleRows = await db.Database.SqlQueryRaw<string>(
            "SELECT id::text || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') || '|' || coalesce(current_state, '-') AS \"Value\" FROM core.firewall_rules")
            .ToListAsync();
        Assert.Contains($"{ruleDeleting}|Exists|Deleted|Delete|enabled", ruleRows);
        Assert.Contains($"{ruleCreating}|NotCreated|Exists|Create|-", ruleRows);
        Assert.Contains($"{ruleLogged}|Exists|Exists|-|logged", ruleRows);
    }
}
