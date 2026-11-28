using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class FunctionSecretRepository(TitanDbContext db) : IFunctionSecretRepository
{
    private IQueryable<FunctionSecret> WithVersions() => db.FunctionSecrets.Include(s => s.Versions);

    public async Task<FunctionSecret?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await WithVersions().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Name == name, ct);

    public async Task<FunctionSecret?> FindByNameForUpdateAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.function_secrets WHERE tenant_id = {tenantId} AND name = {name} FOR UPDATE", ct);
        return await WithVersions().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Name == name, ct);
    }

    public async Task<List<FunctionSecret>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await WithVersions()
            .Where(s => s.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    public async Task<List<FunctionSecret>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await WithVersions()
            .Where(s => s.TenantId == tenantId && ids.Contains(s.Id))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.FunctionSecrets.CountAsync(s => s.TenantId == tenantId, ct);

    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.FunctionSecrets.AnyAsync(s => s.TenantId == tenantId && s.Name == name, ct);

    public void Add(FunctionSecret secret) => db.FunctionSecrets.Add(secret);

    public void Remove(FunctionSecret secret) => db.FunctionSecrets.Remove(secret);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
