using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

/// In memory, no unique index and no version token: ProviderCommandLeaseDbTests measures those on Postgres.
public class FakeProviderCommandRepository : IProviderCommandRepository
{
    public List<ProviderCommand> Rows { get; } = [];
    public int Saves { get; private set; }

    public Task<ProviderCommand?> FindByKeyAsync(string key, CancellationToken ct) =>
        Task.FromResult(Rows.FirstOrDefault(c => c.Key == key));

    public Task<ProviderCommand?> FindByLeaseAsync(Guid leaseId, CancellationToken ct) =>
        Task.FromResult(Rows.FirstOrDefault(c => c.LeaseId == leaseId));

    public Task<IReadOnlyList<ProviderCommand>> ListClaimableAsync(
        IReadOnlyCollection<string> kinds, DateTimeOffset now, int limit, CancellationToken ct)
    {
        IReadOnlyList<ProviderCommand> rows = Rows
            .Where(c => kinds.Contains(c.Kind) && c.IsClaimable(now))
            .OrderBy(c => c.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(rows);
    }

    public void Add(ProviderCommand command) => Rows.Add(command);

    public Task SaveChangesAsync(CancellationToken ct)
    {
        Saves++;
        return Task.CompletedTask;
    }
}
