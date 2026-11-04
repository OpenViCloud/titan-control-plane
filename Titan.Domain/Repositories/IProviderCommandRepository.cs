using Titan.Domain.Aggregates.ProviderCommand;

namespace Titan.Domain.Repositories;

/// Shares the scope's DbContext with every other repository, so a handler's SaveAsync commits the aggregate and the
/// command it enqueued together (titan-ops architecture/provider-and-worker.md#provider-command-table).
public interface IProviderCommandRepository
{
    /// Tracked. Null when the key never had a row.
    Task<ProviderCommand?> FindByKeyAsync(string key, CancellationToken ct);

    /// Tracked. Null when no row carries this lease.
    Task<ProviderCommand?> FindByLeaseAsync(Guid leaseId, CancellationToken ct);

    /// Tracked, oldest first: rows of these kinds that a lease may take now. Two workers may get the same row; the
    /// Version token lets only one lease commit.
    Task<IReadOnlyList<ProviderCommand>> ListClaimableAsync(
        IReadOnlyCollection<string> kinds, DateTimeOffset now, int limit, CancellationToken ct);

    void Add(ProviderCommand command);

    /// Deletes at once, outside the scope's SaveChanges, the rows ProviderCommand.IsFinishedAndCreatedBefore names;
    /// returns how many.
    Task<int> DeleteFinishedCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
