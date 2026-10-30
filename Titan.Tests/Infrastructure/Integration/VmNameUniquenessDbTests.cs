using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The unique (tenant_id, name) index and VmRepository.ExistsByNameAsync must agree on when a name is free:
/// once the VM is Destroyed, and not before. InMemory enforces no index, so only Postgres can show a create
/// the handler let through failing at the index instead.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmNameUniquenessDbTests(PostgresFixture pg)
{
    private static Vm NewVm(Guid tenant, string name) =>
        Vm.CreateUnchecked(tenant, Guid.NewGuid(), name, "m", "tpl", "cluster-a", 20);

    private async Task SaveAsync(Vm vm)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
    }

    private async Task<bool> NameTakenAsync(Guid tenant, string name)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new VmRepository(db).ExistsByNameAsync(tenant, name);
    }

    private static void AssertUniqueViolation(DbUpdateException ex) =>
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);

    [Fact]
    public async Task TheNameOfADeletedVm_CanBeUsedAgain()
    {
        var tenant = Guid.NewGuid();
        Vm gone = NewVm(tenant, "shared-name");
        gone.MarkRunning("41", "10.0.0.5");
        gone.RequestDelete();
        gone.MarkDeleted();
        await SaveAsync(gone);
        bool takenBefore = await NameTakenAsync(tenant, "shared-name");

        await SaveAsync(NewVm(tenant, "shared-name"));

        Assert.False(takenBefore);
        Assert.True(await NameTakenAsync(tenant, "shared-name"));
    }

    // A failed create is not Deleted: the handler answers VM_NAME_TAKEN, and the index agrees.
    [Fact]
    public async Task TheNameOfAVmInError_StaysTaken()
    {
        var tenant = Guid.NewGuid();
        Vm errored = NewVm(tenant, "held-name");
        errored.MarkRequestFailed("failed before provisioning", null);
        await SaveAsync(errored);

        Assert.True(await NameTakenAsync(tenant, "held-name"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(NewVm(tenant, "held-name")));
        AssertUniqueViolation(ex);
    }

    [Fact]
    public async Task RenamingToTheNameOfADeletedVm_Succeeds()
    {
        var tenant = Guid.NewGuid();
        Vm gone = NewVm(tenant, "old-name");
        gone.MarkRunning("42", "10.0.0.6");
        gone.RequestDelete();
        gone.MarkDeleted();
        Vm live = NewVm(tenant, "live-name");
        await SaveAsync(gone);
        await SaveAsync(live);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Vm stored = await db.Vms.SingleAsync(v => v.Id == live.Id);
            stored.Rename("old-name");
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Assert.Equal("old-name", (await db.Vms.SingleAsync(v => v.Id == live.Id)).Name);
        }
    }

    // Names are per tenant: another tenant's live VM never holds this tenant's name.
    [Fact]
    public async Task TheSameName_InTwoTenants_IsAllowed()
    {
        await SaveAsync(NewVm(Guid.NewGuid(), "same-name"));

        await SaveAsync(NewVm(Guid.NewGuid(), "same-name"));
    }
}
