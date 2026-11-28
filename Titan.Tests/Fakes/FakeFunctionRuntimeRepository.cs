using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory IFunctionRuntimeRepository for unit tests (titan-ops architecture/testing.md#doubles-only-in-unit-tests), the
// conditions of FunctionRuntimeRepository. Only the locked read finds a runtime for writing, as FakeVmTemplateRepository.
public class FakeFunctionRuntimeRepository(params FunctionRuntime[] seed) : IFunctionRuntimeRepository
{
    private readonly List<FunctionRuntime> _items = [.. seed];

    public bool Saved { get; private set; }

    /// The ids the locked read was asked for, in order.
    public List<Guid> Locked { get; } = [];

    public Task<FunctionRuntime?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        Locked.Add(id);
        return Task.FromResult(_items.FirstOrDefault(r => r.Id == id));
    }

    public Task<List<FunctionRuntime>> ListInClusterAsync(string clusterId, CancellationToken ct = default) =>
        Task.FromResult(_items.Where(r => r.Clusters.Any(c => c.ClusterId == clusterId)).ToList());

    public Task<List<FunctionRuntime>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.OrderBy(r => r.Name, StringComparer.Ordinal).ToList());

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saved = true;
        return Task.CompletedTask;
    }
}
