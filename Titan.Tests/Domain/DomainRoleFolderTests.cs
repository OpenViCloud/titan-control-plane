using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Tests.Domain;

// titan-ops architecture/code-structure.md#domain-tree. The role is read from the compiled type, not from the path it
// is being checked against.
public class DomainRoleFolderTests
{
    private const string Aggregates = "Titan.Domain.Aggregates";
    private const string Values = "Titan.Domain.Values";
    private const string ChildEntities = "ChildEntities";
    private const string ValueObjects = "ValueObjects";
    private const string Enums = "Enums";
    private const string Inputs = "Inputs";
    private const string Results = "Results";

    private static readonly string[] RoleFolders = [ChildEntities, ValueObjects, Enums, Inputs, Results];

    [Fact]
    public void AggregateRoot_IsTheOnlyFileAtTheTopOfItsFolder()
    {
        var folders = AggregateFolders();
        Assert.True(folders.Count >= 20, $"Only {folders.Count} aggregate folders found.");

        var offenders = new List<string>();
        foreach (var folder in folders)
        {
            var aggregate = Path.GetFileName(folder);
            var files = Directory.GetFiles(folder, "*.cs").Select(Path.GetFileName).ToList();
            if (files.Count != 1 || files[0] != aggregate + ".cs")
                offenders.Add($"{aggregate}: {string.Join(", ", files)}");
        }

        Assert.True(offenders.Count == 0,
            "An aggregate folder must show its root and nothing else at the top: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void AggregateFolders_HoldOnlyRoleFolders_WithNothingBelowThem()
    {
        var folders = AggregateFolders();
        Assert.NotEmpty(folders);

        var offenders = new List<string>();
        foreach (var folder in folders)
            foreach (var sub in Directory.GetDirectories(folder))
            {
                var relative = Path.GetRelativePath(AggregatesDir(), sub);
                if (!RoleFolders.Contains(Path.GetFileName(sub)))
                    offenders.Add($"{relative} is not a role folder");
                if (Directory.GetDirectories(sub).Length != 0)
                    offenders.Add($"{relative} has a folder below it");
            }

        Assert.True(offenders.Count == 0, string.Join(" | ", offenders));
    }

    [Fact]
    public void EveryAggregateType_LivesInTheFolderOfItsRole()
    {
        var types = typeof(NotFoundException).Assembly.GetExportedTypes()
            .Where(t => !t.IsNested
                        && t.Namespace is not null
                        && t.Namespace.StartsWith(Aggregates + ".", StringComparison.Ordinal))
            .ToList();
        Assert.True(types.Count >= 50, $"Only {types.Count} aggregate types found.");

        var offenders = new List<string>();
        foreach (var type in types)
        {
            var aggregate = type.Namespace!.Split('.')[^1];
            var expected = Path.Combine(AggregatesDir(), aggregate, RoleFolderOf(type), type.Name + ".cs");
            if (!File.Exists(expected))
                offenders.Add($"{type.Name} belongs at {Path.GetRelativePath(AggregatesDir(), expected)}");
        }

        Assert.True(offenders.Count == 0, string.Join(" | ", offenders));
    }

    [Fact]
    public void SharedValueTypes_AreUsedByAtLeastTwoAggregates()
    {
        var values = typeof(NotFoundException).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == Values)
            .ToList();
        Assert.NotEmpty(values);

        var sources = AggregateFolders()
            .Select(folder => (
                Aggregate: Path.GetFileName(folder),
                Code: string.Join('\n', Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                    .SelectMany(File.ReadLines)
                    .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))))
            .ToList();

        var offenders = new List<string>();
        foreach (var type in values)
        {
            var users = sources.Where(s => Regex.IsMatch(s.Code, $@"\b{type.Name}\b")).Select(s => s.Aggregate).ToList();
            if (users.Count < 2)
                offenders.Add($"{type.Name} is used by {users.Count} aggregate(s): {string.Join(", ", users)}");
        }

        Assert.True(offenders.Count == 0,
            "A type only one aggregate uses belongs in that aggregate's folder: " + string.Join(" | ", offenders));
    }

    private static string RoleFolderOf(Type type)
    {
        if (typeof(AggregateRoot).IsAssignableFrom(type)) return "";
        if (typeof(ChildEntity).IsAssignableFrom(type)) return ChildEntities;
        if (typeof(ValueObject).IsAssignableFrom(type)) return ValueObjects;
        // Static classes here are closed sets of constants, or extensions of an enum: the kinds, the stored tokens.
        if (type.IsEnum || (type.IsAbstract && type.IsSealed)) return Enums;
        return ReturnedByAnAggregateMethod(type) ? Results : Inputs;
    }

    private static bool ReturnedByAnAggregateMethod(Type type) =>
        typeof(NotFoundException).Assembly.GetExportedTypes()
            // Skip the type's own methods: a record's compiler-generated Clone returns the record, whatever its role.
            .Where(t => t != type && t.Namespace is not null && t.Namespace.StartsWith(Aggregates + ".", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Any(m => m.ReturnType == type);

    private static List<string> AggregateFolders() => Directory.GetDirectories(AggregatesDir()).ToList();

    private static string AggregatesDir() => Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates");

    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
