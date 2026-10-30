using Titan.Domain.Aggregates.VmTemplate;

namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// The sync of one template in one cluster, admin-facing (titan-ops features/catalog/design.md#template-per-cluster). Incus has
// no state word for an image alias, so only existence, transition and the last give-up are carried
// (titan-ops features/catalog/design.md#template-state-is-existence); the failure detail is the operator's.
public record AdminTemplateClusterQueryResult(
    string ClusterId,
    string CurrentExistence, // NotCreated | Exists | NotFound — what that cluster was last observed to hold
    string DesiredExistence, // Exists — sync is add-only
    string? Transition,      // Syncing while a sync request waits for the worker; null otherwise
    string? RequestFailureReason,
    string? RequestFailureDetail,
    DateTimeOffset? RequestFailedAt,
    string? ExternalTemplateId)
{
    public static AdminTemplateClusterQueryResult From(VmTemplateCluster z)
    {
        string? transition = null;
        if (z.Transition != null)
        {
            transition = z.Transition.ToString();
        }
        return new(z.ClusterId, z.CurrentExistence.ToString(), z.DesiredExistence.ToString(), transition,
            z.RequestFailureReason, z.RequestFailureDetail, z.RequestFailedAt, z.ExternalTemplateId);
    }
}
