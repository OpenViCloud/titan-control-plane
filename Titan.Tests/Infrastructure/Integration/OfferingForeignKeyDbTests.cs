using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops features/catalog/design.md#resources-reference-their-offering — on real Postgres: a VM or volume row
// that names an offering the catalog does not have is refused, and so is deleting an offering something
// still points at. OfferingForeignKeySchemaTests pins the model; this proves the migration built it.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class OfferingForeignKeyDbTests(PostgresFixture pg)
{
    // The refusal must come from THIS foreign key, not from any other constraint the row happens to break.
    private static void AssertForeignKey(Exception? error, string constraint)
    {
        var postgres = Assert.IsType<Npgsql.PostgresException>(error);
        Assert.Equal("23503", postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }

    // The key is matched exactly, the way the create handler asks the repository.
    [Fact]
    public async Task FindById_MatchesTheKeyExactly()
    {
        await using var db = pg.NewDbContext();
        var repo = new Titan.Infrastructure.Repositories.VolumeOfferingRepository(db);

        Assert.NotNull(await repo.FindByIdAsync("standard"));
        Assert.Null(await repo.FindByIdAsync("Standard"));
        Assert.Null(await repo.FindByIdAsync("no-such-offering"));
    }

    [Fact]
    public async Task Volume_WithAnOfferingTheCatalogLacks_IsRefused()
    {
        await using var db = pg.NewDbContext();
        db.Volumes.Add(Volume.CreateUnchecked(Guid.NewGuid(), "orphan-vol", "cluster-a", "no-such-offering", 10));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertForeignKey(ex.InnerException, "FK_volumes_volume_offerings_volume_offering_id");
    }

    [Fact]
    public async Task Vm_WithAnOfferingTheCatalogLacks_IsRefused()
    {
        await using var db = pg.NewDbContext();
        db.Vms.Add(Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "orphan-vm", "no-such-offering", "tpl", "cluster-a", 20));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertForeignKey(ex.InnerException, "FK_vms_vm_offerings_vm_offering_id");
    }

    // The seeded offering a volume points at cannot be deleted from under it. Run in a transaction that is
    // rolled back, so the shared catalog row survives for the other tests.
    [Fact]
    public async Task DeletingAnOfferingAVolumeUses_IsRefused()
    {
        await using var db = pg.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Volumes.Add(Volume.CreateUnchecked(Guid.NewGuid(), "uses-standard", "cluster-a", "standard", 10));
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM core.volume_offerings WHERE id = 'standard'"));
        AssertForeignKey(ex, "FK_volumes_volume_offerings_volume_offering_id");

        await transaction.RollbackAsync();
    }
}
