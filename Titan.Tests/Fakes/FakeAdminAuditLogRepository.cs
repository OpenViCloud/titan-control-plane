using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

/// Staged rows only: a real row is written by the sibling repository's SaveChanges, which this fake does not
/// model, so a handler that throws after Add still shows the row here.
public sealed class FakeAdminAuditLogRepository : IAdminAuditLogRepository
{
    public List<AdminAuditLog> Staged { get; } = [];

    public void Add(AdminAuditLog entry) => Staged.Add(entry);

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<AdminAuditLog>> ListAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult(Staged.Take(limit).ToList());
}
