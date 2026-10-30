using Titan.Domain.Aggregates.Tenant;

namespace Titan.Domain.Repositories;

public interface ITenantRepository
{
    Task<Tenant?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsByOwnerAsync(string keycloakUserId, CancellationToken ct = default);
    Task<(Guid TenantId, TenantStatus Status)?> FindTenantByMemberAsync(string keycloakUserId, CancellationToken ct = default);
    Task<List<Tenant>> ListAllAsync(CancellationToken ct = default);
    void Add(Tenant tenant);
    Task SaveAsync(CancellationToken ct = default);
}
