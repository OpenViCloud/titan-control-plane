namespace Titan.Application.Queries.Compute.FunctionSecret.Contracts;

// A kept version of a function not Deleted that reads the secret, and the secret version it pinned; Running when it is
// the version the function serves, so the customer sees which functions still run an older value.
public record FunctionSecretUseQueryResult(
    Guid FunctionId,
    string FunctionName,
    Guid VersionId,
    int VersionNumber,
    int SecretVersion,
    bool Running);
