namespace Titan.Application.Queries.Compute.FunctionSecret.Contracts;

// A secret as the customer reads it: never a value nor its ciphertext, only the name, the kept versions and which
// function versions point at which of them (titan-ops features/ovc-function/design.md#function-secrets).
public record FunctionSecretQueryResult(
    string Name,
    DateTimeOffset CreatedAt,
    int LatestVersion,
    List<FunctionSecretVersionQueryResult> Versions,
    List<FunctionSecretUseQueryResult> UsedBy);
