namespace Titan.Application.Abstractions;

/// The language of a runtime, which decides the file a zip must hold (titan-ops features/ovc-function/design.md#code-limits).
public enum FunctionCodeLanguage
{
    Python,
    NodeJs,
    Go,
}

/// The code layer one zip became: the zip's own SHA-256 and size, what it held, and the gzip layer written to disk.
/// Digests are "sha256:<64 hex>".
public sealed record FunctionCodeLayer(
    string ZipSha256,
    long ZipBytes,
    long UnpackedBytes,
    int FileCount,
    string DiffId,
    string Digest,
    long Bytes,
    string Path);

/// Checks a customer's zip against the code limits and packs its files, under function/, into one deterministic image
/// layer (titan-ops features/ovc-function/design.md#code-source). It reads the zip as data and runs nothing of it. A zip
/// that breaks a rule throws BusinessException("FUNCTION_CODE_INVALID", reason): the reason is safe to show the customer.
public interface IFunctionCodeInspector
{
    Task<FunctionCodeLayer> PackAsync(string zipPath, FunctionCodeLanguage language, string? handler, string workDir, CancellationToken ct);
}
