using System.Diagnostics;
using System.Security.Cryptography;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Microsoft.Extensions.Options;
using Titan.Application.Options;

namespace Titan.Application.Commands.Platform.BackupTarget;

/// Writes, lists, reads back and deletes one object of its own in the configured target
/// (titan-ops features/backup/design.md#backup-target).
/// A target that fails is a result (ok: false), never an exception; no key pair and a configuration that cannot work are the environment's errors.
public class TestBackupTargetCommandHandler(IOptions<BackupTargetOptions> options, IBackupBucketFactory buckets)
{
    /// Outside the lock rule on postgres/ so the probe can delete what it writes.
    private const string ProbePrefix = "_probe/";
    /// The wait for the whole probe, and the extra wait for its cleanup; properties so a test can shorten them.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<BackupTargetTestResult> HandleAsync(TestBackupTargetCommand command, CancellationToken ct = default)
    {
        BackupBucketConnection connection = options.Value.ToConnection();
        string secret = connection.SecretAccessKey;
        using IBackupBucket bucket = buckets.Open(connection);

        // Random content, so two probes never compare equal by accident.
        string key = $"{ProbePrefix}{Guid.NewGuid():N}";
        byte[] content = RandomNumberGenerator.GetBytes(32);

        var watch = Stopwatch.StartNew();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Timeout);
        Task probe = ProbeAsync(bucket, key, content, wait.Token);
        try
        {
            // WaitAsync too, so an adapter that ignores its token cannot hold the request past the timeout.
            await probe.WaitAsync(wait.Token);
            return new BackupTargetTestResult(true, watch.ElapsedMilliseconds, null);
        }
        catch (BackupBucketException e)
        {
            return Failed(e.Message, secret);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await LetCleanupFinishAsync(probe);
            return Failed($"the bucket did not answer within {Timeout.TotalSeconds:0} seconds", secret);
        }
        catch (OperationCanceledException)
        {
            await LetCleanupFinishAsync(probe);
            throw;
        }
    }

    /// An abandoned probe is still deleting its object; the bucket must not be disposed under it.
    private async Task LetCleanupFinishAsync(Task probe)
    {
        await Task.WhenAny(probe, Task.Delay(CleanupTimeout + TimeSpan.FromMilliseconds(100)));
        if (probe.IsFaulted || probe.IsCanceled)
        {
            // Its failure was already reported as the timeout; observe it so it is not left unobserved.
            _ = probe.Exception;
        }
    }

    private async Task ProbeAsync(IBackupBucket bucket, string key, byte[] content, CancellationToken ct)
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
            await bucket.DeleteAsync(key, ct);
            IReadOnlyList<string> after = await bucket.ListKeysAsync(key, ct);
            if (after.Contains(key))
            {
                throw new BackupBucketException("the object is still listed after its delete");
            }
        }
        catch
        {
            // Leave nothing behind, even when the write itself timed out and may have landed, or a delete failed once.
            await TryDeleteAsync(bucket, key);
            throw;
        }
    }

    private async Task TryDeleteAsync(IBackupBucket bucket, string key)
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
            // Out of time for cleanup too; the object stays and the probe still reports why it failed.
        }
    }

    /// The adapter writes messages that carry no secret; this is the second lock, for an adapter that gets it wrong.
    private static BackupTargetTestResult Failed(string message, string secret) =>
        new(false, null, message.Replace(secret, "***"));
}
