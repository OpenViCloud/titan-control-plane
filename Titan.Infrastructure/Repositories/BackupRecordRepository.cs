using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class BackupRecordRepository(TitanDbContext db) : IBackupRecordRepository
{
    public Task<List<BackupRecord>> ListAsync(string kind, string resourceName, CancellationToken ct = default) =>
        db.Backups.Where(r => r.Kind == kind && r.ResourceName == resourceName).ToListAsync(ct);

    public void Add(BackupRecord record) => db.Backups.Add(record);

    public void Remove(BackupRecord record) => db.Backups.Remove(record);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
