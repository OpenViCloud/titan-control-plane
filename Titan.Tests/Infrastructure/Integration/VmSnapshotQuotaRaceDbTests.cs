using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The per-VM snapshot cap is counted on the VM's own snapshots, under a lock on the VM row
/// (titan-ops architecture/code-structure.md#child-entity): two requests that both read the VM one below its cap take turns,
/// and the second counts the first. Category=Integration — its own CI step; needs real Postgres, since the lock is a
/// Postgres row lock and InMemory has none (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmSnapshotQuotaRaceDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task TwoCreatesOneBelowTheCap_OneIs202_TheOtherIsQuotaExceeded_AndTheCapHolds()
    {
        Tenant tenant = TestTenant.Active($"vms-race-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "vms-race@titan.local");
        string offeringId = await SeedOfferingAsync();
        Vm vm = Vm.CreateUnchecked(tenant.Id, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..12], offeringId, "tpl-1", "cluster-1", 20);
        vm.MarkCreated($"vm{Guid.NewGuid():N}"[..20]); // on Incus, running: snapshottable
        vm.MarkObserved(VmState.Running, "10.0.0.5");
        int cap = tenant.Quota.MaxVmSnapshotsPerVm;
        for (int i = 0; i < cap - 1; i++)
        {
            vm.AddSnapshotUnchecked($"snap-{i}").MarkCreated($"{vm.ExternalId}/s{i}");
        }
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Vms.Add(vm);
            await seed.SaveChangesAsync();
        }

        // Two requests, two contexts, started together: each would read cap - 1 snapshots without the lock.
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        Task<Exception?> a = TryCreateAsync(first, tenant.Id, vm.Id, "race-a");
        Task<Exception?> b = TryCreateAsync(second, tenant.Id, vm.Id, "race-b");
        Exception?[] outcomes = await Task.WhenAll(a, b);

        Exception? refused = Assert.Single(outcomes, o => o != null);
        Assert.Single(outcomes, o => o == null);
        BusinessException quota = Assert.IsType<BusinessException>(refused);
        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", quota.Code);

        await using TitanDbContext read = pg.NewDbContext();
        Vm stored = (await new VmRepository(read).FindByIdAsync(vm.Id))!;
        Assert.Equal(cap, stored.SnapshotQuotaUsage);
        Assert.Equal(cap, stored.Snapshots.Count);
    }

    // The foreign key is the database's half of "a snapshot is the VM's": a row pointing at no VM is refused.
    [Fact]
    public async Task ASnapshotWhoseVmDoesNotExist_IsRefusedByTheDatabase()
    {
        Vm orphanHolder = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), $"gone-{Guid.NewGuid():N}"[..12], "std", "tpl-1", "cluster-1", 20);
        VmSnapshot orphan = orphanHolder.AddSnapshotUnchecked("orphan");

        await using TitanDbContext db = pg.NewDbContext();
        db.VmSnapshots.Add(orphan); // the VM itself is never added

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // vms.vm_offering_id is a foreign key (titan-ops features/catalog/design.md#resources-reference-their-offering).
    private async Task<string> SeedOfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        VmOffering? existing = await db.VmOfferings.FirstOrDefaultAsync();
        if (existing != null)
        {
            return existing.Id;
        }
        throw new InvalidOperationException("The migrated database seeds the VM offering catalog; none found.");
    }

    private static async Task<Exception?> TryCreateAsync(TitanDbContext db, Guid tenantId, Guid vmId, string name)
    {
        var caller = Callers.TenantMember(tenantId);
        var handler = new CreateVmSnapshotCommandHandler(
            new VmRepository(db), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        try
        {
            await handler.HandleAsync(new CreateVmSnapshotCommand(vmId, name));
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
