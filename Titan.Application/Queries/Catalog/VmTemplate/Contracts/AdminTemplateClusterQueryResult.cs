using Titan.Domain.Aggregates.VmTemplate;

namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// The sync of one template in one cluster, admin-facing (titan-ops features/catalog/design.md#template-per-cluster). Incus has
// no state word for an image alias, so the one status and the last give-up are carried
// (titan-ops features/catalog/design.md#template-state-is-existence); the failure detail is the operator's.
public record AdminTemplateClusterQueryResult(
    string ClusterId,
    string Status, // Syncing | Available | NotFound | Failed
    string? RequestFailureReason,
    string? RequestFailureDetail,
    DateTimeOffset? RequestFailedAt,
    string? ExternalTemplateId)
{
    public static AdminTemplateClusterQueryResult From(VmTemplateCluster z) =>
        new(z.ClusterId, z.Status.ToString(), z.Operation.FailureReason, z.Operation.FailureDetail, z.Operation.FailedAt,
            z.ExternalTemplateId);
}
