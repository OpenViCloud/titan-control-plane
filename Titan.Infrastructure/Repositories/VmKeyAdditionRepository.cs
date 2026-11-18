using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Read side of the key addition table (titan-ops architecture/code-structure.md#child-entity); writes go through
/// VmRepository.
public class VmKeyAdditionRepository(TitanDbContext db) : IVmKeyAdditionRepository
{
    public async Task<List<VmKeyAddition>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmKeyAdditions
            .Where(a => a.VmId == vmId)
            .OrderByDescending(a => a.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<VmKeyAddition?> FindByIdAsync(Guid keyAdditionId, CancellationToken ct = default) =>
        await db.VmKeyAdditions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == keyAdditionId, ct);
}
