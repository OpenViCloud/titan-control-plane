namespace Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;

// The report of a runtime sync's own command in one function cluster
// (titan-ops architecture/resource-lifecycle.md#callback-routes); the runtime, the cluster and the request come from the
// route. Outcome is "Done" or "Failed". No externalId: the image digest is the runtime's id on the cluster.
public record EndFunctionRuntimeSyncRequestCommand(
    Guid RuntimeId,
    string ClusterId,
    Guid RequestId,
    string Outcome,
    string? Reason = null,
    string? Detail = null);
