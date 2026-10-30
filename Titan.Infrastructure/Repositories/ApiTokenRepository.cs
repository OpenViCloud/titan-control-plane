using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class ApiTokenRepository(TitanDbContext db) : IApiTokenRepository
{
    public async Task<ApiToken?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<ApiToken?> FindBySecretHashAsync(string secretHash, CancellationToken ct = default) =>
        await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.SecretHash == secretHash, ct);

    public async Task<List<ApiToken>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.ApiTokens
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.ApiTokens.CountAsync(t => t.TenantId == tenantId, ct);

    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.ApiTokens.AnyAsync(t => t.TenantId == tenantId && t.Name == name, ct);

    public async Task<bool> ExistsBySecretHashAsync(string secretHash, CancellationToken ct = default) =>
        await db.ApiTokens.AnyAsync(t => t.SecretHash == secretHash, ct);

    public void Add(ApiToken token) => db.ApiTokens.Add(token);

    public void Remove(ApiToken token) => db.ApiTokens.Remove(token);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
