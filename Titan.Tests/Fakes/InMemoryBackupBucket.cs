using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// A bucket in memory. Each call can be told to fail, to lie, or to hang, so the probe's reaction is what the test sees.
public sealed class InMemoryBackupBucket : IBackupBucket
{
    public Dictionary<string, byte[]> Objects { get; } = new();
    public List<string> PutKeys { get; } = new();
    public Func<string, Task>? OnPut { get; set; }
    public Func<string, byte[], byte[]>? OnGet { get; set; }
    public Func<string, IReadOnlyList<string>, IReadOnlyList<string>>? OnList { get; set; }
    public Func<string, Task>? OnDelete { get; set; }

    public void Dispose()
    {
    }

    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        if (OnPut is not null)
        {
            await OnPut(key);
        }
        PutKeys.Add(key);
        Objects[key] = content;
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        byte[] stored = Objects[key];
        return Task.FromResult(OnGet is null ? stored : OnGet(key, stored));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct)
    {
        IReadOnlyList<string> keys = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return Task.FromResult(OnList is null ? keys : OnList(prefix, keys));
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (OnDelete is not null)
        {
            await OnDelete(key);
        }
        Objects.Remove(key);
    }
}
