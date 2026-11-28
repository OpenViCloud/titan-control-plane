using Titan.Domain.Aggregates.FunctionRuntime;

namespace Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;

// The sync of one runtime in one function cluster, admin-facing, as a template's (AdminTemplateClusterQueryResult): the one
// status and the last give-up; the failure detail is the operator's.
public record AdminFunctionRuntimeClusterQueryResult(
    string ClusterId,
    string Status, // Syncing | Available | NotFound | Failed
    string? RequestFailureReason,
    string? RequestFailureDetail,
    DateTimeOffset? RequestFailedAt)
{
    public static AdminFunctionRuntimeClusterQueryResult From(FunctionRuntimeCluster c) =>
        new(c.ClusterId, c.Status.ToString(), c.Operation.FailureReason, c.Operation.FailureDetail, c.Operation.FailedAt);
}
