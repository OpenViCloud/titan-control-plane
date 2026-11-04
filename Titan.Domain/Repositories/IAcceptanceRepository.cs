using Titan.Domain.Aggregates.Acceptance;

namespace Titan.Domain.Repositories;

/// Append-only click-wrap consent proof (titan-ops features/legal/design.md#consent-gate-per-user): Acceptance is
/// immutable because there is no update or delete path, not because of a runtime check.
public interface IAcceptanceRepository
{
    /// Keyed on (Keycloak user, key, version): accepting a different version does not satisfy the current gate.
    Task<bool> ExistsAsync(string keycloakUserId, string documentKey, string documentVersion, CancellationToken ct = default);

    Task AddAsync(Acceptance acceptance, CancellationToken ct = default);

    Task<IReadOnlyList<Acceptance>> ListByKeycloakUserIdAsync(string keycloakUserId, CancellationToken ct = default);
}
