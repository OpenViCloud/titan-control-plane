using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Function;

/// One uploaded version of a function's code and configuration (titan-ops features/ovc-function/design.md#function-versions):
/// immutable once made, except that it is discarded when it falls out of the kept versions. A child of Function, made
/// only by Function.AddVersion and removed only by Function.ForgetCode.
public class FunctionVersion : IEntity
{
    public Guid Id { get; private set; }
    public Guid FunctionId { get; private set; }
    /// 1, 2, 3… in upload order within the function.
    public int Number { get; private set; }
    public Guid RuntimeId { get; private set; }
    public string RuntimeName { get; private set; } = default!;
    /// The GHCR image of the runtime, pinned by digest, as it was at the upload: the function image is built on its layers.
    public string RuntimeImage { get; private set; } = default!;
    /// "module.function" for Python and Node.js; null for Go, whose bootstrap registers its own handler.
    public string? Handler { get; private set; }
    public int MemoryMiB { get; private set; }
    public int TimeoutSeconds { get; private set; }
    /// "functions/<tenantId>/<functionId>/<versionId>.zip" in the function bucket, the source of truth for the code
    /// (titan-ops features/ovc-function/design.md#code-storage).
    public string CodeKey { get; private set; } = default!;
    public string CodeSha256 { get; private set; } = default!;
    public long CodeBytes { get; private set; }
    public long UnpackedBytes { get; private set; }
    public int FileCount { get; private set; }
    /// The function image's manifest digest, "sha256:<64 hex>".
    public string ImageDigest { get; private set; } = default!;
    /// What the function cluster's kubelets pull, pinned by ImageDigest.
    public string Image { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    /// Set when the version falls out of the kept versions; the cleanup job then removes its zip, its image and the row.
    public DateTimeOffset? DiscardedAt { get; private set; }

    private FunctionVersion() { }

    /// CPU in proportion to memory, one vCPU per 1024 MiB (titan-ops features/ovc-function/design.md#function-limits).
    public int CpuMillicores => MemoryMiB * 1000 / 1024;

    public bool IsDiscarded => DiscardedAt != null;

    internal static FunctionVersion Create(Guid functionId, int number, FunctionVersionSpec spec, DateTimeOffset at) => new()
    {
        Id = spec.VersionId,
        FunctionId = functionId,
        Number = number,
        RuntimeId = spec.RuntimeId,
        RuntimeName = spec.RuntimeName,
        RuntimeImage = spec.RuntimeImage,
        Handler = spec.Handler,
        MemoryMiB = spec.MemoryMiB,
        TimeoutSeconds = spec.TimeoutSeconds,
        CodeKey = spec.CodeKey,
        CodeSha256 = spec.CodeSha256,
        CodeBytes = spec.CodeBytes,
        UnpackedBytes = spec.UnpackedBytes,
        FileCount = spec.FileCount,
        ImageDigest = spec.ImageDigest,
        Image = spec.Image,
        CreatedAt = at,
    };

    internal void Discard(DateTimeOffset at)
    {
        DiscardedAt ??= at;
    }
}
