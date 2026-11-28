using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

// Every state condition below is the SQL twin of a plain bool on Function
// (titan-ops architecture/domain-methods.md#plain-code); FunctionRepositoryStateTests runs every shape through both.
public class FunctionRepository(TitanDbContext db) : IFunctionRepository
{
    private IQueryable<Function> WithVersions() => db.Functions.Include(f => f.Versions);

    // Untracked, so the locked read that follows in the same scope reads the row again instead of handing back what
    // this one tracked (ComputeOwnership.RequireFunctionAsync).
    public async Task<Function?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await WithVersions().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<Function?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.functions WHERE id = {id} FOR UPDATE", ct);
        return await WithVersions().FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    // !Function.IsDeleted
    public async Task<List<Function>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await WithVersions()
            .Where(f => f.TenantId == tenantId && f.Status != FunctionStatus.Deleted)
            .AsNoTracking()
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);

    // Function.OccupiesQuota
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Functions.CountAsync(f => f.TenantId == tenantId && f.Status != FunctionStatus.Deleted, ct);

    // Function.IsReconcilable
    public async Task<List<Function>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default) =>
        await db.Functions
            .Where(f => f.ClusterId == clusterId && f.ExternalId != null && f.Status != FunctionStatus.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Function.VersionsToClean is not empty
    public async Task<List<Function>> ListWithCodeToCleanAsync(CancellationToken ct = default) =>
        await WithVersions()
            .Where(f => f.Versions.Any(v => f.Status == FunctionStatus.Deleted || v.DiscardedAt != null))
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(Function function) => db.Functions.Add(function);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
