using Titan.Domain.Aggregates.Platform.BackupTarget;

namespace Titan.Domain.Repositories.Platform;

public interface IBackupTargetRepository
{
    /// Null until an admin has set the target. Tracked: the caller may Replace it and SaveAsync.
    Task<BackupTarget?> FindAsync(CancellationToken ct = default);

    void Add(BackupTarget target);

    Task SaveAsync(CancellationToken ct = default);
}
