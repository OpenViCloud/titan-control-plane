namespace Titan.Application.Queries.Compute.Function.Contracts;

// The function worker's read of what a version's secrets hold, at its deploy (titan-ops features/ovc-function/design.md#function-secrets).
public record GetFunctionSecretValuesQuery(Guid FunctionId, Guid VersionId);
