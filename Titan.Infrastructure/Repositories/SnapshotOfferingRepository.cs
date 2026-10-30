using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.SnapshotOffering;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class SnapshotOfferingRepository(TitanDbContext db) : ISnapshotOfferingRepository
{
    public async Task<SnapshotOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
        await db.SnapshotOfferings.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<List<SnapshotOffering>> ListAllAsync(CancellationToken ct = default) =>
        await db.SnapshotOfferings.AsNoTracking().ToListAsync(ct);
}
