using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Function;

/// One secret a function version reads, pinned at the upload (titan-ops features/ovc-function/design.md#function-secrets):
/// the variable it goes into, the secret by id and by the name it had, and the secret's version number. "latest" is
/// never stored: it is resolved to a number when the version is made, so a later value reaches the function only through
/// a new version.
public sealed record FunctionSecretRef : ValueObject
{
    public string EnvName { get; private init; } = default!;
    public Guid SecretId { get; private init; }
    public string SecretName { get; private init; } = default!;
    public int Version { get; private init; }

    private FunctionSecretRef() { }

    public static FunctionSecretRef Of(string envName, Guid secretId, string secretName, int version)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "a secret's versions start at 1");
        }
        return new FunctionSecretRef { EnvName = envName, SecretId = secretId, SecretName = secretName, Version = version };
    }
}
