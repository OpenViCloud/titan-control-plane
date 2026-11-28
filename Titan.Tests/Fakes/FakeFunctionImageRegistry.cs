using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// The code registry in memory: the runtime copies made, the function images pushed (by digest) and deleted.
public class FakeFunctionImageRegistry : IFunctionImageRegistry
{
    public const string PullHost = "localhost:5100";

    public List<string> RuntimeCopies { get; } = [];
    public HashSet<string> Images { get; } = [];
    public List<string> DeletedImages { get; } = [];
    public bool Fails { get; set; }
    public string NextDigest { get; set; } = "sha256:" + new string('f', 64);

    public Task EnsureRuntimeCopyAsync(string runtimeName, string runtimeImage, CancellationToken ct)
    {
        ThrowIfFailing();
        RuntimeCopies.Add(runtimeName);
        return Task.CompletedTask;
    }

    public Task<string> PushFunctionImageAsync(string runtimeName, string runtimeImage, Guid tenantId, Guid functionId,
        Guid versionId, FunctionCodeLayer layer, CancellationToken ct)
    {
        ThrowIfFailing();
        Images.Add(NextDigest);
        return Task.FromResult(NextDigest);
    }

    public Task<bool> HasFunctionImageAsync(Guid tenantId, Guid functionId, string digest, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Images.Contains(digest));
    }

    public Task DeleteFunctionImageAsync(Guid tenantId, Guid functionId, string digest, CancellationToken ct)
    {
        ThrowIfFailing();
        Images.Remove(digest);
        DeletedImages.Add(digest);
        return Task.CompletedTask;
    }

    public string PullReference(Guid tenantId, Guid functionId, string digest) => $"{PullHost}/fn/{tenantId}/{functionId}@{digest}";

    private void ThrowIfFailing()
    {
        if (Fails)
        {
            throw new FunctionArtifactException("The registry did not answer.");
        }
    }
}
