using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VMs and volumes meets the migration that marks the ones an admin restored from a backup
/// (titan-ops features/backup/design.md#restore-as-new-resource): every VM and volume there reads as not restored, the
/// columns' default, so it goes on counting toward quota and being billed; one saved as restored reads it back. Down
/// drops both columns. The rows before the migration are written in SQL: the model maps columns this schema does not
/// have yet. The offerings are the catalog's seeded ones ('m', 'standard').
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class RestoredFromBackupMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261124091735_NetworkOpensSshWhenCreated";

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
INSERT INTO core.vms (id, tenant_id, network_id, name, status, vm_offering_id, template_id, cluster_id, disk_size_gb, external_id, created_at)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"vm-" + id.ToString("N")[..8]}, 'Running', 'm', 'tpl', 'cluster-a', 20,
        {"vm" + id.ToString("N")[..20]}, now())");
        return id;
    }

    private async Task<Guid> SeedAVolumeAsync()
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.volumes (id, tenant_id, name, cluster_id, volume_offering_id, size_gb, status, current_attached_elsewhere, external_id, created_at)
VALUES ({id}, {Guid.NewGuid()}, {"vol-" + id.ToString("N")[..8]}, 'cluster-a', 'standard', 10, 'Available', false,
        {"vol" + id.ToString("N")[..20]}, now())");
        return id;
    }

    private async Task<int> RestoredColumnsAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name IN ('vms', 'volumes') AND column_name = 'restored_from_backup'")
            .SingleAsync();
    }

    [Fact]
    public async Task EveryVmAndVolumeThere_IsNotRestored_SoItStillCountsAndIsBilled()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedAVmAsync();
        Guid volumeId = await SeedAVolumeAsync();

        await MigrateToLatestAsync();

        await using TitanDbContext db = NewDbContext();
        Vm vm = await db.Vms.SingleAsync(v => v.Id == vmId);
        Volume volume = await db.Volumes.SingleAsync(v => v.Id == volumeId);
        Assert.Equal((false, false), (vm.RestoredFromBackup, volume.RestoredFromBackup));
        Assert.Equal((true, true), (vm.OccupiesQuota, vm.IsBilled));
        Assert.Equal((true, true), (volume.OccupiesQuota, volume.IsBilled));
    }

    [Fact]
    public async Task AVmAndAVolumeSavedAsRestored_ReadItBack_AndOnesSavedAsCreatedReadFalse()
    {
        await MigrateToLatestAsync();
        Guid tenantId = Guid.NewGuid();
        Vm sourceVm = Vm.Create(tenantId, Guid.NewGuid(), "db-1", "m", "tpl", "cluster-a", 20);
        sourceVm.MarkRunning("vm-source", "10.0.0.5");
        Volume sourceVolume = Volume.Create(tenantId, "data-1", "cluster-a", "standard", 10);
        sourceVolume.MarkCreated("vol-source");
        Vm restoredVm = Vm.RestoreFromBackup(sourceVm, "db-1-restored");
        Volume restoredVolume = Volume.RestoreFromBackup(sourceVolume, "data-1", restoredVm.Id);
        await using (TitanDbContext write = NewDbContext())
        {
            write.AddRange(sourceVm, sourceVolume, restoredVm, restoredVolume);
            await write.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        Assert.True((await read.Vms.SingleAsync(v => v.Id == restoredVm.Id)).RestoredFromBackup);
        Assert.False((await read.Vms.SingleAsync(v => v.Id == sourceVm.Id)).RestoredFromBackup);
        Volume readVolume = await read.Volumes.SingleAsync(v => v.Id == restoredVolume.Id);
        Assert.True(readVolume.RestoredFromBackup);
        Assert.Equal(restoredVm.Id, readVolume.DesiredAttachedVmId);
        Assert.False((await read.Volumes.SingleAsync(v => v.Id == sourceVolume.Id)).RestoredFromBackup);
    }

    [Fact]
    public async Task Down_DropsBothColumns()
    {
        await MigrateToLatestAsync();
        int after = await RestoredColumnsAsync();

        await MigrateToAsync(MigrationBefore);

        Assert.Equal((2, 0), (after, await RestoredColumnsAsync()));
    }
}
