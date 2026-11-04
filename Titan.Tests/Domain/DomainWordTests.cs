namespace Titan.Tests.Domain;

/// The Domain speaks of operations, not requests (titan-ops domain/ubiquitous-language.md#operation): no file under
/// Titan.Domain, by path or by content, holds the word "request" in any case. The one exception is the error code
/// INVALID_REQUEST, a string clients read on the API.
///
/// Silent about: the word in Titan.Application, Titan.Infrastructure and the worker contract (requestId, the
/// pending_request_* columns), which are outside the Domain.
public class DomainWordTests
{
    private const string ErrorCode = "INVALID_REQUEST";

    [Fact]
    public void NoDomainFile_HoldsTheWordRequest()
    {
        string domain = Path.Combine(RepoRoot.ControlPlane, "Titan.Domain");
        List<string> files = Directory.EnumerateFiles(domain, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count > 50, $"the scan found only {files.Count} Domain files");

        var offenders = new List<string>();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(RepoRoot.ControlPlane, file);
            if (relative.Contains("request", StringComparison.OrdinalIgnoreCase))
                offenders.Add(relative);

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Replace(ErrorCode, "", StringComparison.Ordinal);
                if (line.Contains("request", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} place(s) in the Domain say \"request\": " + string.Join(", ", offenders.Take(40)));
    }
}
