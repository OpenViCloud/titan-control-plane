using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VMs meets the migration that gives a VM its stop timeout (titan-ops features/vm/design.md#vm-stop-timeout):
/// every VM there waits 120 s, the column's default, and a VM saved with another value reads it back. Down drops the
/// column. The row before the migration is written in SQL: the model maps the column this schema does not have yet.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmStopTimeoutMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261116123247_ConsoleTickets";

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

    private async Task<Guid> SeedAVmAsync()
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_offerings (id, name, vcpu_count, memory_mb, price_per_hour)
VALUES ('m-stop', 'm', 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING;
INSERT INTO core.vms (id, tenant_id, network_id, name, status, vm_offering_id, template_id, cluster_id, disk_size_gb, created_at)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"vm-" + id.ToString("N")[..8]}, 'Running', 'm-stop', 'tpl', 'cluster-a', 20, now())");
        return id;
    }

    private async Task<int> StopTimeoutColumnsAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'vms' AND column_name = 'stop_timeout_seconds'")
            .SingleAsync();
    }

    [Fact]
    public async Task EveryVmThere_Waits120Seconds()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedAVmAsync();

        await MigrateToLatestAsync();

        await using TitanDbContext db = NewDbContext();
        Vm vm = await db.Vms.SingleAsync(v => v.Id == vmId);
        Assert.Equal(Vm.DefaultStopTimeoutSeconds, vm.StopTimeoutSeconds);
    }

    [Fact]
    public async Task AVmSavedWithAStopTimeout_ReadsItBack()
    {
        await MigrateToLatestAsync();
        await using (TitanDbContext seed = NewDbContext())
        {
            await seed.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.vm_offerings (id, name, vcpu_count, memory_mb, price_per_hour) VALUES ('m-stop', 'm', 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        }
        Vm vm = Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "db-1", "m-stop", "tpl", "cluster-a", 20,
            stopTimeoutSeconds: Vm.MaxStopTimeoutSeconds);
        await using (TitanDbContext write = NewDbContext())
        {
            write.Vms.Add(vm);
            await write.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        Assert.Equal(Vm.MaxStopTimeoutSeconds, (await read.Vms.SingleAsync(v => v.Id == vm.Id)).StopTimeoutSeconds);
    }

    [Fact]
    public async Task Down_DropsTheColumn()
    {
        await MigrateToLatestAsync();
        int after = await StopTimeoutColumnsAsync();

        await MigrateToAsync(MigrationBefore);

        Assert.Equal((1, 0), (after, await StopTimeoutColumnsAsync()));
    }
}
