using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory stub: SaveAsync moves staged adds and removes into Saved, so a test can tell "staged" from "committed".
public class FakeApiTokenRepository : IApiTokenRepository
{
    public List<ApiToken> Saved { get; } = new();
    private readonly List<ApiToken> _added = new();
    private readonly List<ApiToken> _removed = new();
    public int SaveCount { get; private set; }

    public Task<ApiToken?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Saved.FirstOrDefault(t => t.Id == id));

    public Task<ApiToken?> FindBySecretHashAsync(string secretHash, CancellationToken ct = default) =>
        Task.FromResult(Saved.FirstOrDefault(t => t.SecretHash == secretHash));

    public Task<List<ApiToken>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Saved.Where(t => t.TenantId == tenantId).OrderBy(t => t.CreatedAt).ToList());

    public Task<int> CountByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Saved.Count(t => t.TenantId == tenantId));

    public Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        Task.FromResult(Saved.Any(t => t.TenantId == tenantId && t.Name == name));

    public Task<bool> ExistsBySecretHashAsync(string secretHash, CancellationToken ct = default) =>
        Task.FromResult(Saved.Any(t => t.SecretHash == secretHash));

    public void Add(ApiToken token) => _added.Add(token);

    public void Remove(ApiToken token) => _removed.Add(token);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saved.AddRange(_added);
        _added.Clear();
        foreach (ApiToken token in _removed)
        {
            Saved.Remove(token);
        }

        _removed.Clear();
        SaveCount++;
        return Task.CompletedTask;
    }
}
