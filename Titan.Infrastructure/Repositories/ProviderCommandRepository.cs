using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Shares the scope's DbContext with the other repositories, so a handler's SaveAsync writes its aggregate and the
/// command it enqueued in one transaction (titan-ops architecture/provider-and-worker.md#provider-command-table).
public class ProviderCommandRepository(TitanDbContext db) : IProviderCommandRepository
{
    public async Task<ProviderCommand?> FindByKeyAsync(string key, CancellationToken ct)
    {
        // A handler may enqueue the same key twice in one scope before saving; the second call must see the first.
        ProviderCommand? staged = db.ProviderCommands.Local.FirstOrDefault(c => c.Key == key);
        if (staged != null)
        {
            return staged;
        }
        return await db.ProviderCommands.FirstOrDefaultAsync(c => c.Key == key, ct);
    }

    public Task<ProviderCommand?> FindByLeaseAsync(Guid leaseId, CancellationToken ct) =>
        db.ProviderCommands.FirstOrDefaultAsync(c => c.LeaseId == leaseId, ct);

    public async Task<IReadOnlyList<ProviderCommand>> ListClaimableAsync(
        IReadOnlyCollection<string> kinds, DateTimeOffset now, int limit, CancellationToken ct)
    {
        List<ProviderCommand> rows = await db.ProviderCommands
            .Where(c => kinds.Contains(c.Kind))
            .Where(c => (c.Status == ProviderCommandStatus.Pending && c.AvailableAt <= now)
                        || (c.Status == ProviderCommandStatus.Leased && c.LeaseUntil <= now))
            .OrderBy(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows;
    }

    public void Add(ProviderCommand command) => db.ProviderCommands.Add(command);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another lease or report changed the row between load and save.
            throw new ConcurrencyConflictException("PROVIDER_COMMAND_CONFLICT",
                "The provider command changed concurrently; read it again.", ex);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A worker retried PUT leases/{id} while its first PUT was still in flight and both leased a row: the
            // lease_id index lets one keep it.
            throw new ConcurrencyConflictException("PROVIDER_COMMAND_CONFLICT",
                "Another request took this lease first.", ex);
        }
    }
}
