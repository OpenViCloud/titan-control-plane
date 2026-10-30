using Titan.Domain.Aggregates.SshKey;

namespace Titan.Domain.Repositories;

public interface ISshKeyRepository
{
    Task<SshKey?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<SshKey>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// The keys among `ids` that belong to the tenant; a foreign or unknown id is simply missing from the result.
    Task<List<SshKey>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> ExistsByFingerprintAsync(Guid tenantId, string fingerprint, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    void Add(SshKey key);
    void Remove(SshKey key);
    Task SaveAsync(CancellationToken ct = default);
}
