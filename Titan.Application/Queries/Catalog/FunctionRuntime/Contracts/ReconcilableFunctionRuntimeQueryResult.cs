using Titan.Domain.Aggregates.FunctionRuntime;
using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;

// What the reconciler of one function cluster needs to look for a runtime's image on every Ready node and to report only
// what changed (OpenViCloud/titan-function-worker:resource/runtime/reconcile.go). Internal only.
public record ReconcilableFunctionRuntimeQueryResult(Guid Id, string Image, string CurrentExistence)
{
    // The repository lists only runtimes with a row in the cluster, so a missing one here is a broken query.
    public static ReconcilableFunctionRuntimeQueryResult From(FunctionRuntimeAggregate r, string clusterId)
    {
        FunctionRuntimeCluster cluster = r.Clusters.FirstOrDefault(candidate => candidate.ClusterId == clusterId)
            ?? throw new InvalidOperationException($"Function runtime {r.Id} has no row in cluster {clusterId} and cannot be reconciled.");
        // The worker compares Exists and NotFound only, as the template reconciler does.
        string currentExistence = "NotFound";
        if (cluster.IsAvailable)
        {
            currentExistence = "Exists";
        }
        return new(r.Id, r.Image, currentExistence);
    }
}
