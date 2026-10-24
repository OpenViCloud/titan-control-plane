namespace Titan.Application.Abstractions;

// The four object calls the backup probe needs, over one bucket; disposing it frees the HTTP connection. Every failure of
// the bucket is a BackupBucketException; a caller who cancels gets OperationCanceledException.
public interface IBackupBucket : IDisposable
{
    Task PutAsync(string key, byte[] content, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct);

    /// Every object under the prefix, across pages, or the first `limit` of them.
    Task<IReadOnlyList<BackupBucketObject>> ListObjectsAsync(string prefix, int? limit, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}
