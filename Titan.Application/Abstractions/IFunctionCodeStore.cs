namespace Titan.Application.Abstractions;

/// The bucket that keeps every function version's zip, the source of truth for its code
/// (titan-ops features/ovc-function/design.md#code-storage). A failure of the bucket is a FunctionArtifactException.
public interface IFunctionCodeStore
{
    /// Writes the file at filePath to key, streamed, never read into memory whole.
    Task PutAsync(string key, string filePath, CancellationToken ct);

    /// Removes key; a key already gone is not an error.
    Task DeleteAsync(string key, CancellationToken ct);
}
