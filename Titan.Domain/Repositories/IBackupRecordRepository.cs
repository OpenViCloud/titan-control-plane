using Titan.Domain.Aggregates.BackupRecord;

namespace Titan.Domain.Repositories;

public interface IBackupRecordRepository
{
    /// Tracked: the refresh updates and removes them, then saves.
    Task<List<BackupRecord>> ListAsync(string kind, string resourceName, CancellationToken ct = default);

    /// Every row of these kinds, tracked: the VM and volume backups, all resources and tenants.
    Task<List<BackupRecord>> ListOfKindsAsync(IReadOnlyCollection<string> kinds, CancellationToken ct = default);

    void Add(BackupRecord record);

    void Remove(BackupRecord record);

    Task SaveAsync(CancellationToken ct = default);
}
