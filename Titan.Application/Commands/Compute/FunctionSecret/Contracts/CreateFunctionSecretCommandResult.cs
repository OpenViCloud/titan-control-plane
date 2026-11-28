namespace Titan.Application.Commands.Compute.FunctionSecret.Contracts;

// The one identifier the create made (titan-ops architecture/http-contract.md#create-returns-id): the version, 1. The
// name is the caller's, and the secret's id on every route.
public record CreateFunctionSecretCommandResult(int Version);
