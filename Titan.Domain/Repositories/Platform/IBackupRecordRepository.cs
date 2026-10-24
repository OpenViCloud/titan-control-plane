using Titan.Domain.Aggregates.Platform.BackupRecord;

namespace Titan.Domain.Repositories.Platform;

public interface IBackupRecordRepository
{
    /// Tracked: the refresh updates and removes them, then saves.
    Task<List<BackupRecord>> ListAsync(string kind, string resourceName, CancellationToken ct = default);

    void Add(BackupRecord record);

    void Remove(BackupRecord record);

    Task SaveAsync(CancellationToken ct = default);
}
