using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class SshKeyRepository(TitanDbContext db) : ISshKeyRepository
{
    public async Task<SshKey?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.SshKeys.FirstOrDefaultAsync(k => k.Id == id, ct);

    public async Task<List<SshKey>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.SshKeys
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<SshKey>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.SshKeys
            .Where(k => k.TenantId == tenantId && ids.Contains(k.Id))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.SshKeys.CountAsync(k => k.TenantId == tenantId, ct);

    public async Task<bool> ExistsByFingerprintAsync(Guid tenantId, string fingerprint, CancellationToken ct = default) =>
        await db.SshKeys.AnyAsync(k => k.TenantId == tenantId && k.Fingerprint == fingerprint, ct);

    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.SshKeys.AnyAsync(k => k.TenantId == tenantId && k.Name == name, ct);

    public void Add(SshKey key) => db.SshKeys.Add(key);

    public void Remove(SshKey key) => db.SshKeys.Remove(key);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
