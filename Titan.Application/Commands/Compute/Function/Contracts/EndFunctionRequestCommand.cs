namespace Titan.Application.Commands.Compute.Function.Contracts;

// The report of a function request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes). Both ids
// come from the route: the request id is the one the command carries. Outcome is "Done" or "Failed"; ExternalId is the
// Knative Service the worker made, "fn<26>".
public record EndFunctionRequestCommand(
    Guid FunctionId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
