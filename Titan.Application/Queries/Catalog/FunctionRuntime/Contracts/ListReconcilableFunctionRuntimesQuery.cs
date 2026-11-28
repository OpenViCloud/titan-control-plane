namespace Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;

// The runtime reconciler of one function cluster asks which images to look for on its nodes.
public record ListReconcilableFunctionRuntimesQuery(string ClusterId);
