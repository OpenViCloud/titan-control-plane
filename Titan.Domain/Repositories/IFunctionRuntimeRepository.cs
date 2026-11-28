using Titan.Domain.Aggregates.FunctionRuntime;

namespace Titan.Domain.Repositories;

// Every read loads the cluster rows: a runtime's state lives in them (titan-ops features/ovc-function/design.md#runtimes).
public interface IFunctionRuntimeRepository
{
    /// The runtime with its row locked until this context's next save commits: two sync-alls, or a sync-all and a
    /// report, take turns, and the second sees what the first left waiting
    /// (titan-ops architecture/resource-lifecycle.md#one-key-per-operation).
    Task<FunctionRuntime?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default);
    /// Runtimes with a row in the cluster: what that cluster's runtime reconciler observes.
    Task<List<FunctionRuntime>> ListInClusterAsync(string clusterId, CancellationToken ct = default);
    /// Every runtime, by name.
    Task<List<FunctionRuntime>> ListAllAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
