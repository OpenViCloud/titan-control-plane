using System.Diagnostics;
using System.Security.Cryptography;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Application.Commands.Platform.BackupTarget;

/// Writes, lists, reads back and deletes one object of its own in the saved target
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target).
/// A target that fails is a result (ok: false), never an exception; no target and an unreadable secret are Titan's own errors.
public class TestBackupTargetCommandHandler(
    IBackupTargetRepository repo, IBackupTargetSecretProtector protector, IBackupBucketFactory buckets)
{
    /// Outside the lock rule on postgres/ so the probe can delete what it writes.
    private const string ProbePrefix = "_probe/";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    /// The wait for the whole probe; a property so a test can shorten it.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<BackupTargetTestResult> HandleAsync(TestBackupTargetCommand command, CancellationToken ct = default)
    {
        BackupTargetAggregate target = await repo.FindAsync(ct)
            ?? throw new NotFoundException("BACKUP_TARGET_NOT_FOUND", "No backup target has been set.");
        string secret = protector.Unprotect(target.SecretCipher);
        using IBackupBucket bucket = buckets.Open(
            new BackupBucketConnection(target.Endpoint, target.Region, target.Bucket, target.AccessKeyId, secret));

        // Random content, so two probes never compare equal by accident.
        string key = $"{ProbePrefix}{Guid.NewGuid():N}";
        byte[] content = RandomNumberGenerator.GetBytes(32);

        var watch = Stopwatch.StartNew();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Timeout);
        try
        {
            // WaitAsync too, so an adapter that ignores its token cannot hold the request past the timeout.
            await ProbeAsync(bucket, key, content, wait.Token).WaitAsync(wait.Token);
            return new BackupTargetTestResult(true, watch.ElapsedMilliseconds, null);
        }
        catch (BackupBucketException e)
        {
            return Failed(e.Message, secret);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed($"the bucket did not answer within {Timeout.TotalSeconds:0} seconds", secret);
        }
    }

    private static async Task ProbeAsync(IBackupBucket bucket, string key, byte[] content, CancellationToken ct)
    {
        try
        {
            await bucket.PutAsync(key, content, ct);
            IReadOnlyList<string> listed = await bucket.ListKeysAsync(key, ct);
            if (!listed.Contains(key))
            {
                throw new BackupBucketException("the bucket did not list the object it was just given");
            }
            byte[] readBack = await bucket.GetAsync(key, ct);
            if (!SHA256.HashData(readBack).AsSpan().SequenceEqual(SHA256.HashData(content)))
            {
                throw new BackupBucketException("the object read back does not match what was written (SHA-256 differs)");
            }
        }
        catch
        {
            // Leave nothing behind, even when the write itself timed out and may have landed.
            await TryDeleteAsync(bucket, key);
            throw;
        }

        await bucket.DeleteAsync(key, ct);
        IReadOnlyList<string> after = await bucket.ListKeysAsync(key, ct);
        if (after.Contains(key))
        {
            throw new BackupBucketException("the object is still listed after its delete");
        }
    }

    private static async Task TryDeleteAsync(IBackupBucket bucket, string key)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await bucket.DeleteAsync(key, cleanup.Token);
        }
        catch (BackupBucketException)
        {
            // The failure that brought us here is the one to report.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// The adapter writes messages that carry no secret; this is the second lock, for an adapter that gets it wrong.
    private static BackupTargetTestResult Failed(string message, string secret) =>
        new(false, null, message.Replace(secret, "***"));
}
