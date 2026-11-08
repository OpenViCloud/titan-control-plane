using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;
using Titan.Domain.Aggregates.VmTemplate;

namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// What the reconciler of one cluster needs to match an image alias on that cluster to a Titan template and to
// report only what changed. Internal only — never on a customer path.
public record ReconcilableTemplateQueryResult(Guid Id, string ExternalTemplateId, string CurrentExistence)
{
    // The repository lists only templates with a provider id in the cluster, so a missing one here is a broken
    // query, not a row to skip quietly.
    public static ReconcilableTemplateQueryResult From(VmTemplateAggregate t, string clusterId)
    {
        VmTemplateCluster? cluster = t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == clusterId);
        string? externalTemplateId = null;
        if (cluster != null)
        {
            externalTemplateId = cluster.ExternalTemplateId;
        }
        if (string.IsNullOrEmpty(externalTemplateId))
        {
            throw new InvalidOperationException($"Template {t.Id} has no provider id in cluster {clusterId} and cannot be reconciled.");
        }
        // The worker compares Exists and NotFound only: a row with an alias is one or the other.
        string currentExistence = "NotFound";
        if (cluster!.InstanceStatus == VmTemplateClusterStatus.Available)
        {
            currentExistence = "Exists";
        }
        return new(t.Id, externalTemplateId, currentExistence);
    }
}
