using Titan.Domain.Aggregates.Function;

namespace Titan.Domain.Repositories;

// Every read loads the versions: which one runs, deploys or is kept lives in them.
public interface IFunctionRepository
{
    Task<Function?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// The same, with the function's row locked until this context's next save commits: two operations, or an operation
    /// and a report, take turns, and the second sees what the first left waiting
    /// (titan-ops architecture/resource-lifecycle.md#one-key-per-operation).
    Task<Function?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default);
    /// Not Deleted, newest first.
    Task<List<Function>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// Function.MaxPerTenant: every function not Deleted (Function.OccupiesQuota).
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// The reconciler's set in one function cluster: a Service name, not Deleted (Function.IsReconcilable).
    Task<List<Function>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default);
    /// Functions holding a version whose code the cleanup job removes (Function.VersionsToClean): a discarded version, or
    /// any version of a Deleted function. Untracked.
    Task<List<Function>> ListWithCodeToCleanAsync(CancellationToken ct = default);
    void Add(Function function);
    Task SaveAsync(CancellationToken ct = default);
}
