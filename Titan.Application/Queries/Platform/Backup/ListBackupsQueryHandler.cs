using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Queries.Platform.Backup;

/// The newest backup of each protected resource and its health, from the cache the refresh keeps
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-status-reads-the-bucket).
public class ListBackupsQueryHandler(
    IBackupRecordRepository records, IBackupObservationRepository observations, TimeProvider clock)
{
    public async Task<BackupsQueryResult> HandleAsync(ListBackupsQuery query, CancellationToken ct = default)
    {
        var observation = await observations.FindAsync(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, ct);
        List<BackupRecord> rows = await records.ListAsync(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, ct);

        var (health, reason) = BackupHealthPolicy.ForPostgres(observation, rows, clock.GetUtcNow());
        BackupRecord? newest = rows
            .Where(r => r.Status == BackupRecordStatus.Complete)
            .OrderByDescending(r => r.TakenAt)
            .FirstOrDefault();

        var item = new BackupItemQueryResult(
            BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, null,
            newest?.TakenAt, newest?.SizeBytes, health, reason, observation?.LastWalAt);
        return new BackupsQueryResult(observation?.LastReadAt, new[] { item });
    }
}
