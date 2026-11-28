namespace Titan.Application.Commands.Compute.Function.Contracts;

// The route carries the function; the body (DeployFunctionBody, next to the endpoint) the version.
public record DeployFunctionCommand(Guid FunctionId, Guid VersionId);
