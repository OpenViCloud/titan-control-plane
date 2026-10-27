using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class TenantRepository(TitanDbContext db) : ITenantRepository
{
    public async Task<Tenant?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Tenants.Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<bool> ExistsByOwnerAsync(string keycloakUserId, CancellationToken ct = default) =>
        await db.Set<Member>().AnyAsync(m => m.KeycloakUserId == keycloakUserId, ct);

    public async Task<(Guid TenantId, TenantStatus Status)?> FindTenantByMemberAsync(string keycloakUserId, CancellationToken ct = default)
    {
        var row = await db.Set<Member>()
            .Where(m => m.KeycloakUserId == keycloakUserId)
            .Select(m => new { m.TenantId, m.Tenant.Status })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : (row.TenantId, row.Status);
    }

    public async Task<List<Tenant>> ListAllAsync(CancellationToken ct = default) =>
        await db.Tenants.AsNoTracking().ToListAsync(ct);

    public void Add(Tenant tenant) => db.Tenants.Add(tenant);

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Only a first sign-in inserts here: its tenant lost to a committed row on tenants(name) or
            // tenant_members(keycloak_user_id). Detach the losing inserts: the request's later saves share this context
            // and would resubmit them.
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            throw new ConcurrencyConflictException("TENANT_CREATE_CONFLICT",
                "A committed tenant or member won a unique guard first.", ex);
        }
    }
}
