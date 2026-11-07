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
    private const string Entities = "Entities";
    private const string ValueObjects = "ValueObjects";
    private const string Enums = "Enums";
    private const string Inputs = "Inputs";
    private const string Results = "Results";

    private static readonly string[] RoleFolders = [Entities, ValueObjects, Enums, Inputs, Results];

    [Fact]
    public void TheRootAndItsPartialFiles_AreTheOnlyFilesAtTheTopOfItsFolder()
    {
        var folders = AggregateFolders();
        Assert.True(folders.Count >= 20, $"Only {folders.Count} aggregate folders found.");

        var offenders = new List<string>();
        foreach (var folder in folders)
        {
            var files = new Dictionary<string, string>();
            foreach (var path in Directory.GetFiles(folder, "*.cs"))
                files[Path.GetFileName(path)] = File.ReadAllText(path);
            offenders.AddRange(TopFileOffenders(Path.GetFileName(folder), files));
        }

        Assert.True(offenders.Count == 0,
            "An aggregate folder must show its root, and the root's partial files, and nothing else at the top: "
            + string.Join(" | ", offenders));
    }

    // The top of an aggregate folder holds <Root>.cs, and <Root>.<Part>.cs files that declare partial class <Root>,
    // read from the code outside comments.
    private static List<string> TopFileOffenders(string aggregate, Dictionary<string, string> files)
    {
        var offenders = new List<string>();
        string rootFile = aggregate + ".cs";
        if (!files.ContainsKey(rootFile))
            offenders.Add($"{aggregate}: {rootFile} is missing");

        var partName = new Regex($@"^{aggregate}\.[A-Z][A-Za-z]*\.cs$");
        var partOfTheRoot = new Regex($@"\bpartial\s+class\s+{aggregate}\b");
        foreach (var name in files.Keys.Order(StringComparer.Ordinal))
        {
            if (name == rootFile)
                continue;
            if (!partName.IsMatch(name))
            {
                offenders.Add($"{aggregate}: {name} is not named {aggregate}.<Part>.cs");
                continue;
            }
            var code = string.Join('\n', files[name].Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            if (!partOfTheRoot.IsMatch(code))
                offenders.Add($"{aggregate}: {name} is not a partial file of {aggregate}");
        }
        return offenders;
    }

    [Fact]
    public void TopFileOffenders_AcceptsOnlyTheRootAndItsPartialFiles()
    {
        var files = new Dictionary<string, string>
        {
            ["Vm.cs"] = "public partial class Vm : IAggregateRoot { }",
            ["Vm.Start.cs"] = "namespace X;\npublic partial class Vm\n{\n}",
            ["Vm.Helper.cs"] = "// public partial class Vm\ninternal static class VmHelper { }",
            ["VmStart.cs"] = "public partial class Vm { }",
        };

        Assert.Equal(
            ["Vm: Vm.Helper.cs is not a partial file of Vm", "Vm: VmStart.cs is not named Vm.<Part>.cs"],
            TopFileOffenders("Vm", files));
    }

    [Fact]
    public void TopFileOffenders_NamesAMissingRoot()
    {
        var files = new Dictionary<string, string> { ["Vm.Start.cs"] = "public partial class Vm { }" };

        Assert.Equal(["Vm: Vm.cs is missing"], TopFileOffenders("Vm", files));
    }

    [Fact]
    public void EveryEntityFile_IsNamedAfterItsType_OrIsAPartialFileOfIt()
    {
        var offenders = new List<string>();
        foreach (var folder in AggregateFolders())
        {
            string entities = Path.Combine(folder, Entities);
            if (!Directory.Exists(entities))
                continue;
            var files = new Dictionary<string, string>();
            foreach (var path in Directory.GetFiles(entities, "*.cs"))
                files[Path.GetFileName(path)] = File.ReadAllText(path);
            offenders.AddRange(EntityFileOffenders(Path.GetFileName(folder), files));
        }

        Assert.True(offenders.Count == 0,
            "A file in Entities/ is <Entity>.cs, or <Entity>.<Part>.cs declaring partial class <Entity>: " + string.Join(" | ", offenders));
    }

    // An entity that grows past one screen is split like its root, by operation: <Entity>.<Part>.cs next to <Entity>.cs
    // (titan-ops architecture/code-structure.md#domain-tree), read from the code outside comments.
    private static List<string> EntityFileOffenders(string aggregate, Dictionary<string, string> files)
    {
        var offenders = new List<string>();
        foreach (var name in files.Keys.Order(StringComparer.Ordinal))
        {
            var segments = name.Split('.');
            if (segments.Length == 2)
                continue;
            if (segments.Length != 3)
            {
                offenders.Add($"{aggregate}: {name} is not <Entity>.cs or <Entity>.<Part>.cs");
                continue;
            }
            var code = string.Join('\n', files[name].Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            if (!Regex.IsMatch(code, $@"\bpartial\s+class\s+{segments[0]}\b"))
                offenders.Add($"{aggregate}: {name} is not a partial file of {segments[0]}");
        }
        return offenders;
    }

    [Fact]
    public void EntityFileOffenders_AcceptsOnlyTheEntityAndItsPartialFiles()
    {
        var files = new Dictionary<string, string>
        {
            ["FirewallRule.cs"] = "public partial class FirewallRule : IEntity { }",
            ["FirewallRule.Deletion.cs"] = "namespace X;\npublic partial class FirewallRule\n{\n}",
            ["FirewallRule.Helper.cs"] = "// public partial class FirewallRule\ninternal static class Helper { }",
            ["FirewallRule.Part.Deep.cs"] = "public partial class FirewallRule { }",
        };

        Assert.Equal(
            ["Network: FirewallRule.Helper.cs is not a partial file of FirewallRule",
             "Network: FirewallRule.Part.Deep.cs is not <Entity>.cs or <Entity>.<Part>.cs"],
            EntityFileOffenders("Network", files));
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

    // The request shape of titan-ops architecture/resource-lifecycle.md#shape is shared by design and reaches the aggregates one
    // rollout phase at a time (titan-ops architecture/resource-lifecycle.md#rollout). A shared type a single aggregate uses so
    // far waits here; its row leaves when a second aggregate uses it — the stale check below says so.
    private static readonly Dictionary<string, string> SharedByTheRollout = new(StringComparer.Ordinal)
    {
    };

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
            // A generic's reflection name carries its arity (PendingOperation`1); the source says PendingOperation<VolumeSnapshotOperationKind>.
            string name = type.Name.Split('`')[0];
            var users = sources.Where(s => Regex.IsMatch(s.Code, $@"\b{name}\b")).Select(s => s.Aggregate).ToList();
            if (users.Count < 2 && !SharedByTheRollout.ContainsKey(name))
                offenders.Add($"{name} is used by {users.Count} aggregate(s): {string.Join(", ", users)}");
        }
        var stale = SharedByTheRollout.Keys
            .Where(name => sources.Count(s => Regex.IsMatch(s.Code, $@"\b{name}\b")) >= 2)
            .ToList();
        Assert.True(stale.Count == 0, "SharedByTheRollout rows for types two aggregates now use; delete them: " + string.Join(", ", stale));

        Assert.True(offenders.Count == 0,
            "A type only one aggregate uses belongs in that aggregate's folder: " + string.Join(" | ", offenders));
    }

    private static string RoleFolderOf(Type type)
    {
        if (typeof(IAggregateRoot).IsAssignableFrom(type)) return "";
        if (typeof(IEntity).IsAssignableFrom(type)) return Entities;
        if (typeof(ValueObject).IsAssignableFrom(type)) return ValueObjects;
        // Static classes here are closed sets of constants, or extensions of an enum: the kinds, the stored tokens.
        if (type.IsEnum || (type.IsAbstract && type.IsSealed)) return Enums;
        return ReturnedByAnAggregateMethod(type) ? Results : Inputs;
    }

    // Internal counts: a method only a domain service calls still hands its result out of the
    // aggregate. Property getters do not: an input record's part (LoadBalancerListenerSpec.Port) is read back, not handed out.
    private static bool ReturnedByAnAggregateMethod(Type type) =>
        typeof(NotFoundException).Assembly.GetExportedTypes()
            // Skip the type's own methods: a record's compiler-generated Clone returns the record, whatever its role.
            .Where(t => t != type && t.Namespace is not null && t.Namespace.StartsWith(Aggregates + ".", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => (m.IsPublic || m.IsAssembly) && !m.IsSpecialName)
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
