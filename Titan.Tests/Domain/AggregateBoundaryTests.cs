using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/code-structure.md#aggregate-is-a-folder. Without this guard a type sitting outside an aggregate
// folder has no owner anyone had to decide; a prose table of owners rots, this does not.
public class AggregateBoundaryTests
{
    private const string Root = "Titan.Domain.Aggregates";

    [Fact]
    public void EveryAggregateType_LivesInAnAggregateFolder()
    {
        var offenders = AggregateTypes()
            .Where(t => Segments(t).Length != 1)
            .Select(t => $"{t.Name} in {t.Namespace}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(AggregateTypes());
        Assert.True(offenders.Count == 0,
            "Type(s) outside an aggregate's namespace. The namespace must be "
            + "Titan.Domain.Aggregates.<Aggregate>: no module layer above it, no segment below it: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryAggregateFolder_ContainsItsRoot()
    {
        var byNamespace = AggregateTypes()
            .Where(t => Segments(t).Length == 1)
            .GroupBy(t => t.Namespace!, StringComparer.Ordinal)
            .ToList();

        // No folder found reads identical to every folder holding its root.
        Assert.True(byNamespace.Count >= 20, $"Only {byNamespace.Count} aggregate folders found.");

        var rootless = new List<string>();
        foreach (var group in byNamespace)
        {
            var aggregate = group.Key.Split('.')[^1];
            if (!group.Any(t => t.Name == aggregate && t.IsClass))
                rootless.Add($"{group.Key} (has: {string.Join(", ", group.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal))})");
        }

        Assert.True(rootless.Count == 0,
            "Aggregate folder without a root class of the same name — the folder name IS the claim "
            + "about who owns what is inside it: " + string.Join(" | ", rootless.OrderBy(x => x, StringComparer.Ordinal)));
    }

    [Fact] // so a type cannot hide inside another type's file
    public void EveryAggregateFile_DeclaresExactlyOneType()
    {
        var dir = Path.Combine(FindSolutionDir(), "Titan.Domain", "Aggregates");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var declared = File.ReadLines(file)
                .Count(l => l.StartsWith("public ", StringComparison.Ordinal)
                            && (l.Contains(" class ") || l.Contains(" enum ") || l.Contains(" record ")
                                || l.Contains(" struct ") || l.Contains(" interface ")));

            if (declared != 1)
                offenders.Add($"{Path.GetFileName(file)}: {declared} public types");
        }

        Assert.NotEmpty(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories));
        Assert.True(offenders.Count == 0,
            "File(s) not declaring exactly one public type — an enum riding along in the root's file "
            + "is a type whose owner was never decided: " + string.Join(", ", offenders));
    }

    // ---- machinery ---------------------------------------------------------------------------

    private static IEnumerable<Type> AggregateTypes() =>
        typeof(NotFoundException).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace is not null
                        && t.Namespace.StartsWith(Root + ".", StringComparison.Ordinal));

    private static string[] Segments(Type t) => t.Namespace![(Root.Length + 1)..].Split('.');

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
