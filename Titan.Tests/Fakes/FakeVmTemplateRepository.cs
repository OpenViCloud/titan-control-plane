using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory IVmTemplateRepository for unit tests (titan-ops architecture/testing.md#doubles-only-in-unit-tests). The
// queries mirror VmTemplateRepository's conditions. Only the locked read finds a template: a handler of the request
// lifecycle that read without the row lock would answer 404 here (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
public class FakeVmTemplateRepository(params VmTemplate[] seed) : IVmTemplateRepository
{
    private readonly List<VmTemplate> _items = [.. seed];

    public bool Saved { get; private set; }

    /// The ids the locked read was asked for, in order.
    public List<Guid> Locked { get; } = [];

    public Task<VmTemplate?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<VmTemplate?>(null);

    public Task<VmTemplate?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        Locked.Add(id);
        return Task.FromResult(_items.FirstOrDefault(t => t.Id == id));
    }

    // Deliberately unsorted (insertion order): ordering is the handler's job, so the ordering test fails if it stops
    // sorting.
    public Task<List<VmTemplate>> ListSyncedInClusterAsync(string clusterId, CancellationToken ct = default) =>
        Task.FromResult(_items.Where(t => t.IsSyncedIn(clusterId)).ToList());

    public Task<List<VmTemplate>> ListSyncedInAnyClusterAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.Where(t => t.Clusters.Any(z => z.IsSynced)).ToList());

    public Task<List<VmTemplate>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default) =>
        Task.FromResult(_items.Where(t => t.Clusters.Any(z => z.ClusterId == clusterId && z.ExternalTemplateId != null)).ToList());

    public Task<List<VmTemplate>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.OrderBy(t => t.Name).ToList());

    public void Add(VmTemplate template) => _items.Add(template);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saved = true;
        return Task.CompletedTask;
    }
}
