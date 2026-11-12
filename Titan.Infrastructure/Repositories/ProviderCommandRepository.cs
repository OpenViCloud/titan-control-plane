using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
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

    // ProviderCommand.IsClaimable and HoldsTheLaneOf, written in SQL (titan-ops architecture/domain-methods.md#plain-code),
    // among the kinds of the lease; ProviderCommandRepositoryStateTests runs every shape through both. A worker loop asks
    // for the kinds of one whole lane (titan-ops features/provider/design.md#tenant-lane). Two leases
    // that read at once can still pick two rows of one lane; the unique indexes on leased rows
    // (ProviderCommandConfiguration) let one of them commit.
    public async Task<IReadOnlyList<ProviderCommand>> ListClaimableAsync(
        IReadOnlyCollection<string> kinds, DateTimeOffset now, int limit, CancellationToken ct)
    {
        string[] backup = [.. ProviderCommandKinds.Backup];
        List<ProviderCommand> rows = await db.ProviderCommands
            .Where(c => kinds.Contains(c.Kind))
            .Where(c => (c.Status == ProviderCommandStatus.Pending && c.AvailableAt <= now)
                        || (c.Status == ProviderCommandStatus.Leased && c.LeaseUntil <= now))
            .Where(c => !db.ProviderCommands.Any(other => kinds.Contains(other.Kind)
                                                          && other.Status == ProviderCommandStatus.Leased
                                                          && other.Id != c.Id
                                                          && other.TenantId == c.TenantId
                                                          && backup.Contains(other.Kind) == backup.Contains(c.Kind)))
            .OrderBy(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return rows;
    }

    public void Add(ProviderCommand command) => db.ProviderCommands.Add(command);

    // ProviderCommand.IsFinishedAndCreatedBefore, written in SQL (titan-ops architecture/domain-methods.md#plain-code):
    // an open row is never deleted, however old.
    public Task<int> DeleteFinishedCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        db.ProviderCommands
            .Where(c => (c.Status == ProviderCommandStatus.Done || c.Status == ProviderCommandStatus.Failed) && c.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

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
            // lease_id index lets one keep it. Or two leases took two rows of one lane: an index on leased rows lets one
            // keep its row (ProviderCommandConfiguration).
            throw new ConcurrencyConflictException("PROVIDER_COMMAND_CONFLICT",
                "Another lease came first; ask again.", ex);
        }
    }
}
