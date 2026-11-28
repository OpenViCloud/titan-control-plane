namespace Titan.Application.Commands.Compute.FunctionSecret.Contracts;

// The secret's name is its id on every route; the version is 1.
public record CreateFunctionSecretCommandResult(string Name, int Version);
