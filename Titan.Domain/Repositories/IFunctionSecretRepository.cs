using Titan.Domain.Aggregates.FunctionSecret;

namespace Titan.Domain.Repositories;

// Every read loads the versions: which numbers are kept, and their ciphertext, live in them.
public interface IFunctionSecretRepository
{
    /// The tenant's secret of that name, untracked; null when it has none.
    Task<FunctionSecret?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    /// The same, with the secret's row locked until this context's next save commits: two new values take turns, and
    /// the second numbers itself after the first.
    Task<FunctionSecret?> FindByNameForUpdateAsync(Guid tenantId, string name, CancellationToken ct = default);
    /// The tenant's secrets by name.
    Task<List<FunctionSecret>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// The secrets among `ids` that belong to the tenant, untracked; a foreign or deleted id is missing from the result.
    Task<List<FunctionSecret>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// FunctionSecret.MaxPerTenant: every secret the tenant has.
    Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    void Add(FunctionSecret secret);
    void Remove(FunctionSecret secret);
    Task SaveAsync(CancellationToken ct = default);
}
