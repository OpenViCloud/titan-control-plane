using System.Text;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.Backup.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Commands.Platform.Backup;

/// Reads the bucket of the configured backup target and rewrites the backup cache from it: Postgres under postgres/, VMs
/// and volumes under incus/
/// (titan-ops features/backup/design.md#backup-status-reads-the-bucket). The bucket is the truth: rows it no longer shows are removed, so dropping the
/// table and running this again gives back the same rows. A bucket that cannot be read is recorded as such and the rows stay.
/// Writes nothing to a wallet or a ledger.
public class RefreshBackupStatusCommandHandler(
    IOptions<BackupTargetOptions> targetOptions,
    IBackupBucketFactory buckets,
    IBackupRecordRepository records,
    IBackupObservationRepository observations,
    TimeProvider clock)
{
    private record BaseFolder(string Key, long Size, DateTimeOffset TakenAt, BackupRecordStatus Status);

    private record BucketSnapshot(BackupReadOutcome Outcome, DateTimeOffset? LastWalAt, IReadOnlyList<BaseFolder> Folders);

    public async Task HandleAsync(RefreshBackupStatusCommand command, CancellationToken ct = default)
    {
        // No key pair: an environment without a backup bucket, or one whose Secret is not there yet. Nothing to read, nothing to record.
        if (!targetOptions.Value.HasKeyPair)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        var postgres = await ObservationAsync(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, ct);
        var incus = await ObservationAsync(BackupLayout.IncusObservationKind, BackupLayout.IncusObservationName, ct);

        IBackupBucket bucket;
        try
        {
            bucket = buckets.Open(targetOptions.Value.ToConnection());
        }
        catch (ServiceUnavailableException e)
        {
            postgres.RecordUnreadable(e.Message, now);
            incus.RecordUnreadable(e.Message, now);
            await records.SaveAsync(ct);
            return;
        }
        using (bucket)
        {
            await RefreshPostgresAsync(bucket, postgres, now, ct);
            await RefreshIncusAsync(bucket, incus, now, ct);
        }
        // One DbContext carries both repositories: this save writes the rows and the observations of both passes together.
        await records.SaveAsync(ct);
    }

    private async Task<BackupObservation> ObservationAsync(string kind, string resourceName, CancellationToken ct)
    {
        var observation = await observations.FindAsync(kind, resourceName, ct);
        if (observation is null)
        {
            observation = BackupObservation.Create(kind, resourceName);
            observations.Add(observation);
        }
        return observation;
    }

    private async Task RefreshPostgresAsync(IBackupBucket bucket, BackupObservation observation, DateTimeOffset now, CancellationToken ct)
    {
        List<BackupRecord> known = await records.ListAsync(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, ct);

        BucketSnapshot snapshot;
        try
        {
            snapshot = await ReadAsync(bucket, known, now, ct);
        }
        catch (BackupBucketException e)
        {
            observation.RecordUnreadable(e.Message, now);
            return;
        }
        catch (ServiceUnavailableException e)
        {
            observation.RecordUnreadable(e.Message, now);
            return;
        }

        Apply(snapshot, known, observation, now);
    }

    /// VM and volume backups under incus/ (titan-ops features/backup/design.md#incus-backup): each object is a
    /// row, and a resource's backups past the newest seven are deleted from the bucket first. Deleting here, where the
    /// listing is read, keeps the worker from needing a delete of its own.
    private async Task RefreshIncusAsync(IBackupBucket bucket, BackupObservation observation, DateTimeOffset now, CancellationToken ct)
    {
        List<BackupRecord> known = await records.ListOfKindsAsync(new[] { BackupRecord.VmKind, BackupRecord.VolumeKind }, ct);

        var kept = new List<(IncusBackupKey Key, long Size)>();
        try
        {
            IReadOnlyList<BackupBucketObject> all = await bucket.ListObjectsAsync(BackupLayout.IncusRoot, null, ct);
            var byResource = all
                .Select(o => (Object: o, Key: IncusBackupKey.Parse(o.Key)))
                .Where(b => b.Key is not null)
                .GroupBy(b => (b.Key!.Kind, b.Key.ResourceId));
            foreach (var resource in byResource)
            {
                var newestFirst = resource.OrderByDescending(b => b.Key!.TakenAt).ToList();
                foreach (var old in newestFirst.Skip(BackupLayout.IncusBackupsKept))
                {
                    await bucket.DeleteAsync(old.Object.Key, ct);
                }
                kept.AddRange(newestFirst.Take(BackupLayout.IncusBackupsKept).Select(b => (b.Key!, b.Object.Size)));
            }
        }
        catch (BackupBucketException e)
        {
            observation.RecordUnreadable(e.Message, now);
            return;
        }
        catch (ServiceUnavailableException e)
        {
            observation.RecordUnreadable(e.Message, now);
            return;
        }

        foreach (BackupRecord gone in known.Where(r => !kept.Any(k => IsRowOf(r, k.Key))))
        {
            records.Remove(gone);
        }
        foreach (var (key, size) in kept)
        {
            BackupRecord? existing = known.FirstOrDefault(r => IsRowOf(r, key));
            if (existing is null)
            {
                records.Add(BackupRecord.CreateForTenant(
                    key.Kind, key.ResourceId.ToString(), key.TenantId, key.BackupKey, key.TakenAt, size, now));
            }
            else
            {
                existing.Observe(key.TakenAt, size, BackupRecordStatus.Complete, now);
            }
        }
        observation.RecordRead(BackupReadOutcome.Read, null, now);
    }

    private static bool IsRowOf(BackupRecord row, IncusBackupKey key) =>
        row.Kind == key.Kind && row.ResourceName == key.ResourceId.ToString() && row.BackupKey == key.BackupKey;

    private static async Task<BucketSnapshot> ReadAsync(
        IBackupBucket bucket, List<BackupRecord> known, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<BackupBucketObject> all = await bucket.ListObjectsAsync(BackupLayout.PostgresRoot, null, ct);
        // The cluster that runs now is the serverName that wrote last: after a restore the old name gets nothing new.
        // Ties go to the larger name, so the choice does not depend on the listing's order.
        string? server = all
            .Where(o => BackupLayout.ServerOf(o.Key) is not null)
            .OrderByDescending(o => o.LastModified)
            .ThenByDescending(o => BackupLayout.ServerOf(o.Key), StringComparer.Ordinal)
            .Select(o => BackupLayout.ServerOf(o.Key))
            .FirstOrDefault();
        if (server is null)
        {
            IReadOnlyList<BackupBucketObject> anything = await bucket.ListObjectsAsync("", 1, ct);
            BackupReadOutcome outcome = anything.Count == 0 ? BackupReadOutcome.Empty : BackupReadOutcome.NoBackupPrefix;
            return new BucketSnapshot(outcome, null, Array.Empty<BaseFolder>());
        }

        string serverPrefix = BackupLayout.PostgresRoot + server + "/";
        string basePrefix = serverPrefix + BackupLayout.BaseFolder;
        string walPrefix = serverPrefix + BackupLayout.WalFolder;
        List<BackupBucketObject> objects = all.Where(o => o.Key.StartsWith(serverPrefix, StringComparison.Ordinal)).ToList();

        DateTimeOffset? lastWal = objects
            .Where(o => o.Key.StartsWith(walPrefix, StringComparison.Ordinal))
            .Select(o => (DateTimeOffset?)o.LastModified)
            .Max();

        var folders = new List<BaseFolder>();
        var byFolder = objects
            .Where(o => o.Key.StartsWith(basePrefix, StringComparison.Ordinal))
            .GroupBy(o => o.Key[basePrefix.Length..].Split('/')[0])
            // A marker object or a folder name nobody's backup tool wrote would otherwise fail the whole pass, every pass.
            .Where(g => BackupRecord.IsValidBackupKey(g.Key))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var folder in byFolder)
        {
            long size = folder.Sum(o => o.Size);
            string infoKey = basePrefix + folder.Key + "/" + BackupLayout.BackupInfoFile;
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
