namespace Titan.Application.Abstractions;

// The four object calls the backup probe needs, over one bucket; disposing it frees the HTTP connection. Every failure of
// the bucket is a BackupBucketException; a caller who cancels gets OperationCanceledException.
public interface IBackupBucket : IDisposable
{
    Task PutAsync(string key, byte[] content, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
