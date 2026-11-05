using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database whose VMs still carry create_finished and has_been_running, defaulted false since the VM's request got
/// its own columns, meets the migration that drops them: the columns go, every VM keeps its waiting request. Down puts
/// them back false, and a VM row written without them still inserts. The model maps the columns VmSingleStatus made, which
/// this schema does not have yet, so the rows are written and read in SQL, column names as the schema before
/// VmSingleStatus held them, by necessity. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmDropsItsPreRequestColumnsMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261105044846_NetworkDropsItsRouteColumns";
    private const string ThisMigration = "20261105044857_VmDropsItsPreRequestColumns";

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

    private async Task<int> OldColumnsAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'vms' AND column_name IN ('create_finished', 'has_been_running')")
            .SingleAsync();
    }

    // A VM waiting on its create, as the image of this schema wrote it: it never wrote create_finished nor
    // has_been_running, so the row leaves them to their default.
    private async Task<(Guid VmId, Guid RequestId)> SeedAVmWaitingOnItsCreateAsync()
    {
        Guid id = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour)
VALUES ('m-drop', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING;
INSERT INTO core.vms (id, tenant_id, network_id, name, current_state, desired_state, current_existence, desired_existence,
                      external_id, vm_offering_id, template_id, cluster_id, disk_size_gb, created_at,
                      pending_request_id, pending_request_kind, pending_request_asked_at)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"vm-drop-" + id.ToString("N")[..8]}, NULL, 'Running', 'NotCreated',
        'Exists', NULL, 'm-drop', 'tpl', 'cluster-a', 20, now(), {requestId}, 'Create', now())");
        return (id, requestId);
    }

    [Fact]
    public async Task TheColumnsGo_EveryVmKeepsItsWaitingRequest()
    {
        await MigrateToAsync(MigrationBefore);
        (Guid vmId, Guid requestId) = await SeedAVmWaitingOnItsCreateAsync();

        await MigrateToAsync(ThisMigration);

        Assert.Equal(0, await OldColumnsAsync());
        await using TitanDbContext read = NewDbContext();
        List<string> waiting = await read.Database
            .SqlQuery<string>($"SELECT pending_request_id::text || '|' || pending_request_kind AS \"Value\" FROM core.vms WHERE id = {vmId}")
            .ToListAsync();
        Assert.Equal([$"{requestId}|Create"], waiting);
    }

    [Fact]
    public async Task Down_PutsTheColumnsBackFalse_AndARowWrittenWithoutThemStillInserts()
    {
        await MigrateToAsync(ThisMigration);
        (Guid before, _) = await SeedAVmWaitingOnItsCreateAsync();

        await MigrateToAsync(MigrationBefore);

        Assert.Equal(2, await OldColumnsAsync());
        (Guid after, _) = await SeedAVmWaitingOnItsCreateAsync();
        await using TitanDbContext read = NewDbContext();
        List<bool> olds = await read.Database
            .SqlQuery<bool>($"SELECT (create_finished OR has_been_running) AS \"Value\" FROM core.vms WHERE id IN ({before}, {after})")
            .ToListAsync();
        Assert.Equal([false, false], olds);
    }
}
