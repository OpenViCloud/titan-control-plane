namespace Titan.Application.Commands.Compute.Function.Contracts;

// The function reconciler's observation: whether the Knative Service is in the tenant's namespace. FunctionId comes from
// the route.
public record ObserveFunctionCommand(Guid FunctionId, bool Exists);
