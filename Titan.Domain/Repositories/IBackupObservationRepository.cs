using Titan.Domain.Aggregates.BackupObservation;

namespace Titan.Domain.Repositories;

public interface IBackupObservationRepository
{
    /// Null until a pass over the bucket has run. Tracked.
    Task<BackupObservation?> FindAsync(string kind, string resourceName, CancellationToken ct = default);

    void Add(BackupObservation observation);

    Task SaveAsync(CancellationToken ct = default);
}
