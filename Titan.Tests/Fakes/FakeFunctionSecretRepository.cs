using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory IFunctionSecretRepository for unit tests: SaveAsync moves staged adds and removes into Saved, so a test can
// tell "staged" from "committed". The same objects are handed out by every read, as a tracked context would.
public class FakeFunctionSecretRepository : IFunctionSecretRepository
{
    public List<FunctionSecret> Saved { get; } = [];
    private readonly List<FunctionSecret> _added = [];
    private readonly List<FunctionSecret> _removed = [];
    public int Saves { get; private set; }

    /// The names the locked read was asked for, in order: a test sees the handler asked for the lock.
    public List<string> Locked { get; } = [];

    public Task<FunctionSecret?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        Task.FromResult(Saved.FirstOrDefault(s => s.TenantId == tenantId && s.Name == name));

    public Task<FunctionSecret?> FindByNameForUpdateAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        Locked.Add(name);
        return FindByNameAsync(tenantId, name, ct);
    }

    public Task<List<FunctionSecret>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Saved.Where(s => s.TenantId == tenantId).OrderBy(s => s.Name, StringComparer.Ordinal).ToList());

    public Task<List<FunctionSecret>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult(Saved.Where(s => s.TenantId == tenantId && ids.Contains(s.Id)).ToList());

    public Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Saved.Count(s => s.TenantId == tenantId));

    public Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        Task.FromResult(Saved.Any(s => s.TenantId == tenantId && s.Name == name));

    public void Add(FunctionSecret secret) => _added.Add(secret);

    public void Remove(FunctionSecret secret) => _removed.Add(secret);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saved.AddRange(_added);
        _added.Clear();
        foreach (FunctionSecret secret in _removed)
        {
            Saved.Remove(secret);
        }
        _removed.Clear();
        Saves++;
        return Task.CompletedTask;
    }
}
