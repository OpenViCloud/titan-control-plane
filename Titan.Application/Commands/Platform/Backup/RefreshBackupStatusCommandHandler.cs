using System.Text;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Commands.Platform.Backup;

/// Reads the bucket of the saved backup target and rewrites the backup cache from it
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-status-reads-the-bucket). The bucket is the truth: rows it no longer shows are removed, so dropping the
/// table and running this again gives back the same rows. A bucket that cannot be read is recorded as such and the rows stay.
/// Writes nothing to a wallet or a ledger.
public class RefreshBackupStatusCommandHandler(
    IBackupTargetRepository targets,
    IBackupTargetSecretProtector protector,
    IBackupBucketFactory buckets,
    IBackupRecordRepository records,
    IBackupObservationRepository observations,
    TimeProvider clock)
{
    private record BaseFolder(string Key, long Size, DateTimeOffset TakenAt, BackupRecordStatus Status);

    private record BucketSnapshot(BackupReadOutcome Outcome, DateTimeOffset? LastWalAt, IReadOnlyList<BaseFolder> Folders);

    public async Task HandleAsync(RefreshBackupStatusCommand command, CancellationToken ct = default)
    {
        var target = await targets.FindAsync(ct);
        if (target is null)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        var observation = await observations.FindAsync(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, ct);
        if (observation is null)
        {
            observation = BackupObservation.Create(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName);
            observations.Add(observation);
        }
        List<BackupRecord> known = await records.ListAsync(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, ct);

        BucketSnapshot snapshot;
        try
        {
            string secret = protector.Unprotect(target.SecretCipher);
            using IBackupBucket bucket = buckets.Open(
                new BackupBucketConnection(target.Endpoint, target.Region, target.Bucket, target.AccessKeyId, secret));
            snapshot = await ReadAsync(bucket, known, now, ct);
        }
        catch (BackupBucketException e)
        {
            observation.RecordUnreadable(e.Message, now);
            await records.SaveAsync(ct);
            return;
        }
        catch (ServiceUnavailableException e)
        {
            observation.RecordUnreadable(e.Message, now);
            await records.SaveAsync(ct);
            return;
        }

        Apply(snapshot, known, observation, now);
        // One DbContext carries both repositories: this save writes the rows and the observation together.
        await records.SaveAsync(ct);
    }

    private static async Task<BucketSnapshot> ReadAsync(
        IBackupBucket bucket, List<BackupRecord> known, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<BackupBucketObject> objects = await bucket.ListObjectsAsync(BackupLayout.PostgresPrefix, null, ct);
        if (objects.Count == 0)
        {
            IReadOnlyList<BackupBucketObject> anything = await bucket.ListObjectsAsync("", 1, ct);
            BackupReadOutcome outcome = anything.Count == 0 ? BackupReadOutcome.Empty : BackupReadOutcome.NoBackupPrefix;
            return new BucketSnapshot(outcome, null, Array.Empty<BaseFolder>());
        }

        DateTimeOffset? lastWal = objects
            .Where(o => o.Key.StartsWith(BackupLayout.WalPrefix, StringComparison.Ordinal))
            .Select(o => (DateTimeOffset?)o.LastModified)
            .Max();

        var folders = new List<BaseFolder>();
        var byFolder = objects
            .Where(o => o.Key.StartsWith(BackupLayout.BasePrefix, StringComparison.Ordinal))
            .GroupBy(o => o.Key[BackupLayout.BasePrefix.Length..].Split('/')[0])
            // A marker object or a folder name nobody's backup tool wrote would otherwise fail the whole pass, every pass.
            .Where(g => BackupRecord.IsValidBackupKey(g.Key))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var folder in byFolder)
        {
            long size = folder.Sum(o => o.Size);
            string infoKey = BackupLayout.BasePrefix + folder.Key + "/" + BackupLayout.BackupInfoFile;
            BackupBucketObject? info = folder.FirstOrDefault(o => o.Key == infoKey);
            if (info is null)
            {
                folders.Add(new BaseFolder(folder.Key, size, folder.Max(o => o.LastModified), BackupRecordStatus.Incomplete));
                continue;
            }

            // A backup already seen complete is not read again: barman does not rewrite a finished backup.
            BackupRecord? already = known.FirstOrDefault(r => r.BackupKey == folder.Key && r.Status == BackupRecordStatus.Complete);
            if (already is not null)
            {
                folders.Add(new BaseFolder(folder.Key, size, already.TakenAt, BackupRecordStatus.Complete));
                continue;
            }

            string text = Encoding.UTF8.GetString(await bucket.GetAsync(infoKey, ct));
            if (!BackupInfoText.IsDone(text))
            {
                folders.Add(new BaseFolder(folder.Key, size, info.LastModified, BackupRecordStatus.Incomplete));
                continue;
            }
            // An end time in the future would make the backup look fresh until that moment passes: fall back to the file's own time.
            DateTimeOffset? endTime = BackupInfoText.EndTime(text);
            DateTimeOffset takenAt = endTime is not null && endTime.Value <= now ? endTime.Value : info.LastModified;
            folders.Add(new BaseFolder(folder.Key, size, takenAt, BackupRecordStatus.Complete));
        }
        return new BucketSnapshot(BackupReadOutcome.Read, lastWal, folders);
    }

    private void Apply(BucketSnapshot snapshot, List<BackupRecord> known, BackupObservation observation, DateTimeOffset now)
    {
        foreach (BackupRecord gone in known.Where(r => snapshot.Folders.All(f => f.Key != r.BackupKey)))
        {
            records.Remove(gone);
        }
        foreach (BaseFolder folder in snapshot.Folders)
        {
            BackupRecord? existing = known.FirstOrDefault(r => r.BackupKey == folder.Key);
            if (existing is null)
            {
                records.Add(BackupRecord.Create(
                    BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, folder.Key, folder.TakenAt, folder.Size, folder.Status, now));
            }
            else
            {
                existing.Observe(folder.TakenAt, folder.Size, folder.Status, now);
            }
        }
        observation.RecordRead(snapshot.Outcome, snapshot.LastWalAt, now);
    }
}
