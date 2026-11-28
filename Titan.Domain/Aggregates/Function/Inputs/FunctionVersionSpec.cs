namespace Titan.Domain.Aggregates.Function;

/// What one upload made of a zip, handed to Function.AddVersion: the runtime it was packed on (copied, so a later change
/// of the catalog never changes a version), its configuration, where the zip is kept and what it held, and the function
/// image pushed for it, and its variables, the secret versions it pins and its schedule, empty when it has none
/// (titan-ops features/ovc-function/design.md#function-versions).
public sealed record FunctionVersionSpec(
    Guid VersionId,
    Guid RuntimeId,
    string RuntimeName,
    string RuntimeImage,
    string? Handler,
    int MemoryMiB,
    int TimeoutSeconds,
    string CodeKey,
    string CodeSha256,
    long CodeBytes,
    long UnpackedBytes,
    int FileCount,
    string ImageDigest,
    string Image,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<FunctionSecretRef> SecretRefs,
    string? Schedule);
