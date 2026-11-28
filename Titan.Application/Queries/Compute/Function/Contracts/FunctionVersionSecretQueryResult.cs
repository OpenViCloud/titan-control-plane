namespace Titan.Application.Queries.Compute.Function.Contracts;

// The secret a version's variable reads, by the name it had at the upload, and the number "latest" was pinned to.
public record FunctionVersionSecretQueryResult(string Secret, int Version);
