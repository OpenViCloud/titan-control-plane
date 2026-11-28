using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class FunctionRuntimeRepository(TitanDbContext db) : IFunctionRuntimeRepository
{
    // The runtime's row, not its cluster rows: a cluster row a sync-all adds does not exist yet to be locked.
    public async Task<FunctionRuntime?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.function_runtimes WHERE id = {id} FOR UPDATE", ct);
        return await db.FunctionRuntimes.Include(r => r.Clusters).FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<List<FunctionRuntime>> ListInClusterAsync(string clusterId, CancellationToken ct = default) =>
        await db.FunctionRuntimes
            .Include(r => r.Clusters)
            .Where(r => r.Clusters.Any(c => c.ClusterId == clusterId))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<FunctionRuntime>> ListAllAsync(CancellationToken ct = default) =>
        await db.FunctionRuntimes
            .Include(r => r.Clusters)
            .OrderBy(r => r.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
