namespace Titan.Application.Queries.Compute.Function.Contracts;

// What the function reconciler needs to find the Knative Service in the tenant's namespace and to report only what
// changed (titan-ops features/ovc-function/design.md#function-lifecycle). Internal only.
public record ReconcilableFunctionQueryResult(Guid Id, Guid TenantId, string CurrentExistence);
