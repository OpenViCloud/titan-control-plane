using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VmOfferingRepository(TitanDbContext db) : IVmOfferingRepository
{
    // Tracked, unlike ListAllAsync: the admin price change mutates the instance SaveAsync writes.
    public async Task<VmOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
        await db.VmOfferings.FirstOrDefaultAsync(o => o.Id == id, ct);

    // Unordered: ListVmOfferingsQueryHandler owns the customer-facing size ordering.
    public async Task<List<VmOffering>> ListAllAsync(CancellationToken ct = default) =>
        await db.VmOfferings.AsNoTracking().ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
