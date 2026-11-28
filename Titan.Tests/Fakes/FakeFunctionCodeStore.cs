using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// The function bucket in memory: the keys written and deleted, and keys whose delete fails.
public class FakeFunctionCodeStore : IFunctionCodeStore
{
    public List<string> Put { get; } = [];
    public List<string> Deleted { get; } = [];
    public bool PutFails { get; set; }
    public HashSet<string> DeleteFailsFor { get; } = [];

    public Task PutAsync(string key, string filePath, CancellationToken ct)
    {
        if (PutFails)
        {
            throw new FunctionArtifactException("The function bucket did not answer.");
        }
        Put.Add(key);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (DeleteFailsFor.Contains(key))
        {
            throw new FunctionArtifactException("The function bucket did not answer.");
        }
        Deleted.Add(key);
        return Task.CompletedTask;
    }
}
