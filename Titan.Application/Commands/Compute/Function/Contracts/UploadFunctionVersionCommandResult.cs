namespace Titan.Application.Commands.Compute.Function.Contracts;

// Only the id (titan-ops architecture/http-contract.md#create-returns-id).
public record UploadFunctionVersionCommandResult(Guid VersionId);
