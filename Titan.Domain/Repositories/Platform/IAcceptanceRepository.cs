using Titan.Domain.Aggregates.Platform.Acceptance;

namespace Titan.Domain.Repositories.Platform;

/// Append-only click-wrap consent proof (titan-ops features/legal/design.md#consent-gate-per-user): Acceptance is
/// immutable because there is no update or delete path, not because of a runtime check.
public interface IAcceptanceRepository
{
    /// Keyed on (subject, key, version): accepting a different version does not satisfy the current gate.
    Task<bool> ExistsAsync(string subjectId, string documentKey, string documentVersion, CancellationToken ct = default);

    Task AddAsync(Acceptance acceptance, CancellationToken ct = default);

    Task<IReadOnlyList<Acceptance>> ListBySubjectAsync(string subjectId, CancellationToken ct = default);
}
