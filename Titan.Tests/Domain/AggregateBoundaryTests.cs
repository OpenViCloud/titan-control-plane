using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/code-structure.md#aggregate-is-a-folder. Without this guard a child (InvoiceItem) sitting
// flat in a module is indistinguishable from a root (Wallet); a prose table of roots rots, this does not.
public class AggregateBoundaryTests
{
    private static readonly string[] Modules = ["Billing", "Catalog", "Compute", "Networking", "Platform"];
    private const string Root = "Titan.Domain.Aggregates";

    [Fact]
    public void EveryAggregateType_LivesInAnAggregateFolder()
    {
        var offenders = AggregateTypes()
            .Where(t => Segments(t).Length != 2)
            .Select(t => $"{t.Name} in {t.Namespace}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Type(s) not inside an aggregate folder. The namespace must be "
            + "Titan.Domain.Aggregates.<Module>.<Aggregate> — a type sitting loose in the module is a "
            + "type nobody had to assign an owner to: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryAggregate_SitsUnderAKnownModule()
    {
        var offenders = AggregateTypes()
            .Where(t => Segments(t).Length == 2 && !Modules.Contains(Segments(t)[0]))
            .Select(t => t.Namespace!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, "Unknown module segment: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryAggregateFolder_ContainsItsRoot()
    {
        var byNamespace = AggregateTypes()
            .Where(t => Segments(t).Length == 2)
            .GroupBy(t => t.Namespace!, StringComparer.Ordinal);

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
