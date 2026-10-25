using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// A bucket in memory. Each call can be told to fail, to lie, or to hang, so the probe's reaction is what the test sees.
public sealed class InMemoryBackupBucket : IBackupBucket
{
    public Dictionary<string, byte[]> Objects { get; } = new();
    public Dictionary<string, DateTimeOffset> Modified { get; } = new();
    public List<string> PutKeys { get; } = new();
    public Func<string, Task>? OnPut { get; set; }
    // For a call that honours its token, as the real adapter does: the wait ends when the probe is told to stop.
    public Func<string, CancellationToken, Task>? OnPutWithToken { get; set; }
    public Func<string, byte[], byte[]>? OnGet { get; set; }
    public Func<string, IReadOnlyList<string>, IReadOnlyList<string>>? OnList { get; set; }
    public Func<string, Task>? OnDelete { get; set; }
    public Action<string>? OnListObjects { get; set; }

    public bool Disposed { get; private set; }

    // A real client refuses calls once disposed; the probe's cleanup must finish before that.
    public void Dispose() => Disposed = true;

    // The handler opens (and disposes) one connection per probe; a test that probes twice reuses this one bucket.
    public void Reopen() => Disposed = false;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Disposed, this);

    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (OnPut is not null)
        {
            await OnPut(key);
        }
        if (OnPutWithToken is not null)
        {
            await OnPutWithToken(key, ct);
        }
        PutKeys.Add(key);
        Objects[key] = content;
        Modified[key] = DateTimeOffset.UtcNow;
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        ThrowIfDisposed();
        byte[] stored = Objects[key];
        return Task.FromResult(OnGet is null ? stored : OnGet(key, stored));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct)
    {
        ThrowIfDisposed();
        IReadOnlyList<string> keys = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return Task.FromResult(OnList is null ? keys : OnList(prefix, keys));
    }

    public Task<IReadOnlyList<BackupBucketObject>> ListObjectsAsync(string prefix, int? limit, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (OnListObjects is not null)
        {
            OnListObjects(prefix);
        }
        IEnumerable<BackupBucketObject> found = Objects
            .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(o => new BackupBucketObject(o.Key, o.Value.Length, Modified.GetValueOrDefault(o.Key, DateTimeOffset.UtcNow)))
            .OrderBy(o => o.Key, StringComparer.Ordinal);
        IReadOnlyList<BackupBucketObject> result = (limit is null ? found : found.Take(limit.Value)).ToList();
        return Task.FromResult(result);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (OnDelete is not null)
        {
            await OnDelete(key);
        }
        Objects.Remove(key);
    }
}
