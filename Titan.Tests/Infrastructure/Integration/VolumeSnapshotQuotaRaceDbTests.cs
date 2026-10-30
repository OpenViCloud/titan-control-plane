using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.VolumeOffering;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The per-volume snapshot cap is counted on the volume's own snapshots, under a lock on the volume row
/// (titan-ops architecture/code-structure.md#child-entity): two requests that both read the volume one below its cap take
/// turns, and the second counts the first. Category=Integration — its own CI step; needs real Postgres, since the lock is
/// a Postgres row lock and InMemory has none (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VolumeSnapshotQuotaRaceDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task TwoCreatesOneBelowTheCap_OneIs202_TheOtherIsQuotaExceeded_AndTheCapHolds()
    {
        Tenant tenant = TestTenant.Active($"vs-race-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "vs-race@titan.local");
        string offeringId = await SeedOfferingAsync();
        Volume volume = Volume.CreateUnchecked(tenant.Id, $"vol-{Guid.NewGuid():N}"[..12], "cluster-1", offeringId, 20);
        volume.MarkCreated($"vol{Guid.NewGuid():N}"[..20]);
        int cap = tenant.Quota.MaxVolumeSnapshotsPerVolume;
        for (int i = 0; i < cap - 1; i++)
        {
            volume.AddSnapshotUnchecked($"snap-{i}").MarkCreated($"{volume.ExternalId}/s{i}");
        }
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Volumes.Add(volume);
            await seed.SaveChangesAsync();
        }

        // Two requests, two contexts, started together: each would read cap - 1 snapshots without the lock.
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        Task<Exception?> a = TryCreateAsync(first, tenant.Id, volume.Id, "race-a");
        Task<Exception?> b = TryCreateAsync(second, tenant.Id, volume.Id, "race-b");
        Exception?[] outcomes = await Task.WhenAll(a, b);

        Exception? refused = Assert.Single(outcomes, o => o != null);
        Assert.Single(outcomes, o => o == null);
        BusinessException quota = Assert.IsType<BusinessException>(refused);
        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", quota.Code);

        await using TitanDbContext read = pg.NewDbContext();
        Volume stored = (await new VolumeRepository(read).FindByIdAsync(volume.Id))!;
        Assert.Equal(cap, stored.SnapshotQuotaUsage);
        Assert.Equal(cap, stored.Snapshots.Count);
    }

    // The foreign key is the database's half of "a snapshot is the volume's": a row pointing at no volume is refused.
    [Fact]
    public async Task ASnapshotWhoseVolumeDoesNotExist_IsRefusedByTheDatabase()
    {
        Volume orphanHolder = Volume.CreateUnchecked(Guid.NewGuid(), $"gone-{Guid.NewGuid():N}"[..12], "cluster-1", "std", 20);
        VolumeSnapshot orphan = orphanHolder.AddSnapshotUnchecked("orphan");

        await using TitanDbContext db = pg.NewDbContext();
        db.VolumeSnapshots.Add(orphan); // the volume itself is never added

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // volumes.volume_offering_id is a foreign key (titan-ops features/catalog/design.md#resources-reference-their-offering).
    private async Task<string> SeedOfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        VolumeOffering? existing = await db.VolumeOfferings.FirstOrDefaultAsync();
        if (existing != null)
        {
            return existing.Id;
        }
        throw new InvalidOperationException("The migrated database seeds the volume offering catalog; none found.");
    }

    private static async Task<Exception?> TryCreateAsync(TitanDbContext db, Guid tenantId, Guid volumeId, string name)
    {
        var caller = Callers.TenantMember(tenantId);
        var handler = new CreateVolumeSnapshotCommandHandler(
            new VolumeRepository(db), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        try
        {
            await handler.HandleAsync(new CreateVolumeSnapshotCommand(volumeId, name));
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
