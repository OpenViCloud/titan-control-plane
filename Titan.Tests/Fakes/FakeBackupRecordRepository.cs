using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Domain.Repositories.Platform;

namespace Titan.Tests.Fakes;

public sealed class FakeBackupRecordRepository : IBackupRecordRepository
{
    public List<BackupRecord> Rows { get; } = new();
    public int Saves { get; private set; }

    public Task<List<BackupRecord>> ListAsync(string kind, string resourceName, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(r => r.Kind == kind && r.ResourceName == resourceName).ToList());

    public void Add(BackupRecord record) => Rows.Add(record);

    public void Remove(BackupRecord record) => Rows.Remove(record);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saves++;
        return Task.CompletedTask;
    }
}
