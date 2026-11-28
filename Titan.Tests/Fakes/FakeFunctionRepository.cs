using Titan.Domain.Aggregates.Function;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory IFunctionRepository for unit tests (titan-ops architecture/testing.md#doubles-only-in-unit-tests): each query
// asks the Domain bool whose SQL twin FunctionRepository writes. The same objects are handed out by every read, as a
// tracked context would; FunctionRepositoryStateTests runs the real repository's queries against the same bools.
public class FakeFunctionRepository(params Function[] seed) : IFunctionRepository
{
    public List<Function> Rows { get; } = [.. seed];
    public int Saves { get; private set; }

    /// The ids the locked read was asked for, in order: a test sees the handler asked for the lock.
    public List<Guid> Locked { get; } = [];

    public Task<Function?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(f => f.Id == id));

    public Task<Function?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        Locked.Add(id);
        return FindByIdAsync(id, ct);
    }

    public Task<List<Function>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(f => f.TenantId == tenantId && !f.IsDeleted).OrderByDescending(f => f.CreatedAt).ToList());

    public Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Count(f => f.TenantId == tenantId && f.OccupiesQuota));

    public Task<List<Function>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(f => f.ClusterId == clusterId && f.IsReconcilable).ToList());

    public Task<List<Function>> ListWithCodeToCleanAsync(CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(f => f.VersionsToClean.Any()).ToList());

    public void Add(Function function) => Rows.Add(function);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saves++;
        return Task.CompletedTask;
    }
}
