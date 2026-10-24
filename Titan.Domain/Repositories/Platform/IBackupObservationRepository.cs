using Titan.Domain.Aggregates.Platform.BackupObservation;

namespace Titan.Domain.Repositories.Platform;

public interface IBackupObservationRepository
{
    /// Null until a pass over the bucket has run. Tracked.
    Task<BackupObservation?> FindAsync(string kind, string resourceName, CancellationToken ct = default);

    void Add(BackupObservation observation);

    Task SaveAsync(CancellationToken ct = default);
}
