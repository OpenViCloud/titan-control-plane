namespace Titan.Application.Abstractions;

/// The code registry, through its HTTP API (titan-ops features/ovc-function/design.md#code-storage): a copy of each runtime
/// image under runtime/<name>, and the function images fn/<tenantId>/<functionId>. Only the control plane pushes. A failure
/// of the registry, or of GHCR while copying a runtime, is a FunctionArtifactException.
public interface IFunctionImageRegistry
{
    /// Copies runtimeImage ("ghcr.io/…@sha256:<64 hex>", one linux/amd64 manifest) into runtime/<runtimeName> unless that
    /// digest is already there.
    Task EnsureRuntimeCopyAsync(string runtimeName, string runtimeImage, CancellationToken ct);

    /// Pushes fn/<tenantId>/<functionId>, tag versionId: the runtime copy's layers mounted across repositories, the code
    /// layer on top, the runtime's config with the layer's diff id. Returns the manifest's digest, "sha256:<64 hex>".
    Task<string> PushFunctionImageAsync(string runtimeName, string runtimeImage, Guid tenantId, Guid functionId, Guid versionId,
        FunctionCodeLayer layer, CancellationToken ct);

    Task<bool> HasFunctionImageAsync(Guid tenantId, Guid functionId, string digest, CancellationToken ct);

    /// Deletes the manifest; one already gone is not an error.
    Task DeleteFunctionImageAsync(Guid tenantId, Guid functionId, string digest, CancellationToken ct);

    /// The name the function cluster's kubelets pull: "<pull host>/fn/<tenantId>/<functionId>@<digest>".
    string PullReference(Guid tenantId, Guid functionId, string digest);
}

/// A named failure of the code bucket or the registry. The message is written by the adapter and is safe to show an admin:
/// it never carries a secret or a token.
public class FunctionArtifactException(string message) : Exception(message);
