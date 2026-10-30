using Titan.Domain.Aggregates.ApiToken;

namespace Titan.Domain.Repositories;

public interface IApiTokenRepository
{
    Task<ApiToken?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<ApiToken?> FindBySecretHashAsync(string secretHash, CancellationToken ct = default);
    Task<List<ApiToken>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    Task<bool> ExistsBySecretHashAsync(string secretHash, CancellationToken ct = default);
    void Add(ApiToken token);
    void Remove(ApiToken token);
    Task SaveAsync(CancellationToken ct = default);
}
