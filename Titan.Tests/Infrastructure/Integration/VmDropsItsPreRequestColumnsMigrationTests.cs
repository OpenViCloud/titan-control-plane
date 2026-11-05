using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database whose VMs still carry create_finished and has_been_running, defaulted false since the VM's request got
/// its own columns, meets the migration that drops them: the columns go, every VM keeps its waiting request. Down puts
/// them back false, and a VM the model writes then still saves. The column names below are the schema before it, by
/// necessity. Category=Integration — its own CI step; needs real Postgres.
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

    private async Task<Vm> SaveAVmAsync()
    {
        Vm vm = Vm.Create(Guid.NewGuid(), Guid.NewGuid(), $"vm-drop-{Guid.NewGuid():N}"[..16], "m-drop", "tpl", "cluster-a", 20);
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('m-drop', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return vm;
    }

    [Fact]
    public async Task TheColumnsGo_EveryVmKeepsItsWaitingRequest()
    {
        await MigrateToAsync(MigrationBefore);
        Vm vm = await SaveAVmAsync();

        await MigrateToAsync(ThisMigration);

        Assert.Equal(0, await OldColumnsAsync());
        await using TitanDbContext read = NewDbContext();
        Vm stored = await read.Vms.SingleAsync(v => v.Id == vm.Id);
        Assert.Equal(VmOperationKind.Create, stored.Pending!.Kind);
    }

    [Fact]
    public async Task Down_PutsTheColumnsBackFalse_AndTheModelStillSavesAVm()
    {
        await MigrateToAsync(ThisMigration);
        Vm before = await SaveAVmAsync();

        await MigrateToAsync(MigrationBefore);

        Assert.Equal(2, await OldColumnsAsync());
        Vm after = await SaveAVmAsync();
        await using TitanDbContext read = NewDbContext();
        List<bool> olds = await read.Database
            .SqlQuery<bool>($"SELECT (create_finished OR has_been_running) AS \"Value\" FROM core.vms WHERE id IN ({before.Id}, {after.Id})")
            .ToListAsync();
        Assert.Equal([false, false], olds);
    }
}
