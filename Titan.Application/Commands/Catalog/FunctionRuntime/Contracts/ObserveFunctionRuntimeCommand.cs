namespace Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;

// The runtime reconciler's observation of one function cluster: whether every Ready node holds the image. Runtime and
// cluster come from the route.
public record ObserveFunctionRuntimeCommand(Guid RuntimeId, string ClusterId, bool Exists);
