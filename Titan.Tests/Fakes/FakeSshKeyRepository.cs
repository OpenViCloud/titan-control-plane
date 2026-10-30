using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory stub: SaveAsync moves staged adds and removes into Saved, so a test can tell "staged" from "committed".
public class FakeSshKeyRepository : ISshKeyRepository
{
    public List<SshKey> Saved { get; } = new();
    private readonly List<SshKey> _added = new();
    private readonly List<SshKey> _removed = new();
    public int SaveCount { get; private set; }

    public Task<SshKey?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Saved.FirstOrDefault(k => k.Id == id));

    public Task<List<SshKey>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Saved.Where(k => k.TenantId == tenantId).OrderBy(k => k.CreatedAt).ToList());

    public Task<List<SshKey>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult(Saved.Where(k => k.TenantId == tenantId && ids.Contains(k.Id)).ToList());

    public Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Saved.Count(k => k.TenantId == tenantId));

    public Task<bool> ExistsByFingerprintAsync(Guid tenantId, string fingerprint, CancellationToken ct = default) =>
        Task.FromResult(Saved.Any(k => k.TenantId == tenantId && k.Fingerprint == fingerprint));

    public Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        Task.FromResult(Saved.Any(k => k.TenantId == tenantId && k.Name == name));

    public void Add(SshKey key) => _added.Add(key);

    public void Remove(SshKey key) => _removed.Add(key);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saved.AddRange(_added);
        _added.Clear();
        foreach (SshKey key in _removed)
        {
            Saved.Remove(key);
        }

        _removed.Clear();
        SaveCount++;
        return Task.CompletedTask;
    }
}
