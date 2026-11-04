using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Acceptance;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class AcceptanceRepository(TitanDbContext db) : IAcceptanceRepository
{
    public Task<bool> ExistsAsync(string keycloakUserId, string documentKey, string documentVersion, CancellationToken ct = default) =>
        db.LegalAcceptances
            .AsNoTracking()
            .AnyAsync(a => a.KeycloakUserId == keycloakUserId
                       && a.DocumentKey == documentKey
                       && a.DocumentVersion == documentVersion, ct);

    public async Task AddAsync(Acceptance acceptance, CancellationToken ct = default)
    {
        db.LegalAcceptances.Add(acceptance);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The (user, key, version) unique index rejected a racing double-accept. One row already stands:
            // detach ours and treat it as success.
            db.Entry(acceptance).State = EntityState.Detached;
        }
    }

    public async Task<IReadOnlyList<Acceptance>> ListByKeycloakUserIdAsync(string keycloakUserId, CancellationToken ct = default) =>
        await db.LegalAcceptances
            .AsNoTracking()
            .Where(a => a.KeycloakUserId == keycloakUserId)
            .ToListAsync(ct);
}
