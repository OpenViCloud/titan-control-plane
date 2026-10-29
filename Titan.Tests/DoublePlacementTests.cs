using System.Text.RegularExpressions;

namespace Titan.Tests;

// titan-ops architecture/testing.md#doubles-only-in-unit-tests — a test that runs on Postgres (Category=Integration) or
// through the real host (Category=FullHost) uses the real implementation of everything it touches. The scan reads
// those test files and the helpers next to them. It reads text: it does not see a double reached through a
// helper outside that set, or one built without these names.
public class DoublePlacementTests
{
    private static readonly (Regex Pattern, string Why)[] Forbidden =
    [
        (new Regex(@"\busing Moq;"), "uses Moq"),
        (new Regex(@"\bMock<"), "builds a Moq mock"),
        (new Regex(@"\bTitan\.Tests\.Fakes\b"), "uses a fake from Titan.Tests/Fakes"),
        (new Regex(@"\bUseInMemoryDatabase\("), "runs on the InMemory provider"),
        (new Regex(@"\bservices\.Replace\("), "replaces a service of the host"),
        (new Regex(@"\bConfigureTestServices\("), "replaces services of the host"),
    ];

    // Interfaces a class in these files may implement, each with its reason.
    private static readonly Dictionary<string, string> AllowedInterfaces = new()
    {
        ["IAsyncLifetime"] = "xUnit fixture lifetime",
        ["ICollectionFixture"] = "xUnit fixture sharing",
        ["IClassFixture"] = "xUnit fixture sharing",
        ["IAsyncDisposable"] = "cleanup",
        ["IDisposable"] = "cleanup",
        ["ILoggerProvider"] = "a log sink added next to the host's own: it observes, it replaces nothing",
        ["ILogger"] = "the sink's logger",
    };

    private static readonly Regex ClassWithBases = new(@"\bclass\s+\w+(?:<[^>]*>)?(?:\([^)]*\))?\s*:\s*([^{]+)\{");

    private static IReadOnlyList<string> Scope()
    {
        string tests = Path.Combine(RepoRoot.ControlPlane, "Titan.Tests");
        return Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                bool tagged = text.Contains("[Trait(\"Category\", \"Integration\")]")
                              || text.Contains("[Trait(\"Category\", \"FullHost\")]");
                bool helper = f.Contains(Path.Combine("Infrastructure", "Integration") + Path.DirectorySeparatorChar)
                              || Path.GetFileName(f) == "RealHostFactory.cs";
                return tagged || helper;
            })
            .ToList();
    }

    [Fact]
    public void Scope_HoldsTheIntegrationAndFullHostTests()
    {
        var names = Scope().Select(Path.GetFileName).ToList();

        Assert.Contains("ProviderCommandLeaseDbTests.cs", names);
        Assert.Contains("OpenApiSpecTests.cs", names);
        Assert.Contains("RealHostFactory.cs", names);
        Assert.True(names.Count >= 31, $"expected at least the 21 Integration and 10 FullHost files, scanned {names.Count}");
    }

    [Fact]
    public void IntegrationAndFullHostTests_UseNoTestDouble()
    {
        var offenders = new List<string>();
        foreach (string file in Scope())
        {
            string text = File.ReadAllText(file);
            string name = Path.GetRelativePath(RepoRoot.ControlPlane, file);
            foreach (var (pattern, why) in Forbidden)
            {
                if (pattern.IsMatch(text))
                {
                    offenders.Add($"{name} {why}");
                }
            }
            foreach (Match declaration in ClassWithBases.Matches(text))
            {
                foreach (string baseType in declaration.Groups[1].Value.Split(','))
                {
                    string bare = Regex.Replace(baseType.Trim(), @"[<(].*$", "");
                    bool isInterface = Regex.IsMatch(bare, @"^I[A-Z]");
                    if (isInterface && !AllowedInterfaces.ContainsKey(bare))
                    {
                        offenders.Add($"{name} implements {bare} in a test class");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
}
