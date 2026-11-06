using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Titan.Tests.Domain;

// WHERE the quota rule lives (titan-ops architecture/code-structure.md#quota-gate) is a placement rule, and placement is
// not observable from behaviour, so these guards read source. A handler that re-grew its own
// `const int MaxVolumes = 10`, or skipped the gate, would pass every other test of a different handler.
//
// What they do NOT see, stated because a green run reads like "every create is checked":
//   - a cap read from configuration, or passed in as a method argument
//   - they match TEXT. A constant written as `10` inline, or computed, slips through
//   - a creator in Titan.Infrastructure: only Titan.Application and Titan.Domain are read
//   - a creator outside a method or constructor body: a property, a field initializer
//   - branches: a gate call inside an `if` counts as asked
public class QuotaOwnershipTests
{
    private const string ApplicationDir = "Titan.Application";
    private const string GateFile = "TenantQuotaGate.cs";

    // Each pair of projects has more files than this; a walk that finds fewer is looking in the wrong place.
    private const int FewestFilesInTheTwoProjects = 300;

    // Regex source generators do not run here, so a [GeneratedRegex] partial has no implementation part.
    private const string GeneratedRegexHasNoImplementation = "CS8795";

    // A new resource under quota adds a row here. Network's public IPv6 cap applies only when asked for, which this
    // does not read: it asks that the handler knows the kind.
    private static readonly Dictionary<string, string[]> KindsEachCreatorNeeds = new()
    {
        ["Vm.Create"] = ["Vm"],
        ["Network.Create"] = ["Network", "PublicIpv6"],
        ["Volume.Create"] = ["Volume"],
        ["LoadBalancer.Create"] = ["LoadBalancer"],
        ["SshKey.Create"] = ["SshKey"],
        ["ApiToken.Create"] = ["ApiToken"],
        ["Vm.AddSnapshot"] = ["VmSnapshot"],
        ["Volume.AddSnapshot"] = ["VolumeSnapshot"],
        ["Network.AddFirewallRule"] = ["FirewallRule"],
    };

    // Folders under Titan.Application that are NOT modules. Named so the exclusion is visible rather
    // than implied — and so that adding one is a decision, not an accident.
    //
    // The branches are here for a different reason than the others (titan-ops architecture/code-structure.md#application-tree):
    // they ARE scanned, but as BRANCHES whose module sits one level in (Commands/Compute/Vm), not as modules.
    // A branch missing from this list is read as a module name (TheScanReachesTheHandlerTree turns red).
    private static readonly string[] Branches = ["Commands", "Queries", "Services"];
    private static readonly string[] NonModuleDirs =
        ["Abstractions", "Exceptions", "Options", "bin", "obj", .. Branches];

    // Page-size caps, not tenant resource quota. ListAdminAuditLogQueryHandler.MaxLimit=200 and
    // ListActivityEventsQueryHandler.MaxLimit=50 bound how many rows one API response may carry; they are not
    // something an admin would ever set per customer. Excluded by name so the exclusion is visible, not implied.
    private static readonly string[] AllowedConstants = ["MaxLimit"];

    private static IEnumerable<string> ModuleDirs() =>
        Directory
            .EnumerateDirectories(Path.Combine(RepoRoot.ControlPlane, ApplicationDir))
            .Where(d => !NonModuleDirs.Contains(Path.GetFileName(d)));

    // Any module folder left at the root PLUS the three branches: a quota constant compiled into a
    // handler is exactly as wrong as one compiled into a service, so every tree is read.
    private static IEnumerable<string> ServiceFiles() =>
        ModuleDirs()
            .Concat(Branches.Select(b => Path.Combine(RepoRoot.ControlPlane, ApplicationDir, b)))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories));

    [Fact]
    public void NoApplicationService_HoldsItsOwnQuotaConstant()
    {
        var files = ServiceFiles().ToList();
        Assert.NotEmpty(files); // 0 files scanned reads identical to 0 offenders found

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var m in Regex.Matches(File.ReadAllText(file),
                         @"const\s+int\s+(\w*(?:Max|Quota)\w*)\s*=\s*(\d+)"))
            {
                var match = (Match)m;
                var name = match.Groups[1].Value;
                if (AllowedConstants.Contains(name)) continue;
                offenders.Add($"{Path.GetFileName(file)}: const int {name} = {match.Groups[2].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Application service holds its own resource cap. Caps belong to TenantQuota (docs/architecture/tenancy-and-admin.md#tenant-quota) so "
            + "one customer's limits are one row, not five compiled-in numbers: "
            + string.Join(" | ", offenders));
    }

    [Fact]
    public void NoApplicationService_ComparesAgainstACap_ButTheGate()
    {
        var files = ServiceFiles().ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            if (Path.GetFileName(file) == GateFile)
            {
                continue;
            }
            var src = File.ReadAllText(file);
            // `something >= tenant.Quota.MaxX` / `>= tenant.MaxX`
            foreach (var m in Regex.Matches(src, @"[<>]=?\s*\w+\.(?:Quota\.)?Max\w+"))
                offenders.Add($"{Path.GetFileName(file)}: {((Match)m).Value.Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "Application code compares against a cap outside the gate. The handler COUNTS, the gate DECIDES "
            + "(titan-ops architecture/code-structure.md#quota-gate): "
            + string.Join(" | ", offenders));
    }

    [Fact] // the rule left the aggregate: the tenant cannot be asked any more
    public void TenantHasNoQuotaCheck()
    {
        var tenant = typeof(Titan.Domain.Aggregates.Tenant.Tenant);
        const System.Reflection.BindingFlags everyMethod = System.Reflection.BindingFlags.Public
                                                           | System.Reflection.BindingFlags.NonPublic
                                                           | System.Reflection.BindingFlags.Instance;

        // The guard has to see the type it is asserting about; a renamed aggregate would otherwise
        // make this pass by finding nothing.
        Assert.NotEmpty(tenant.GetMethods(everyMethod));
        Assert.DoesNotContain(tenant.GetMethods(everyMethod), m => m.Name == "EnsureHasRoom");
    }

    // ---- Nothing stops a handler from skipping the gate at compile time, so this reads the methods of
    // Titan.Application and Titan.Domain through the compiler: a call that creates a resource under quota must come
    // after the gate.

    [Fact]
    public void EveryCreator_AsksTheGateFirst()
    {
        var seen = new HashSet<string>();
        List<string> offenders = CreatorsThatSkipTheGate(ApplicationCompilation(), seen);

        Assert.True(seen.SetEquals(KindsEachCreatorNeeds.Keys),
            $"Calls seen: {string.Join(", ", seen)}. A renamed member would otherwise pass by finding nothing.");
        Assert.True(offenders.Count == 0, $"{offenders.Count} calls:\n{string.Join("\n", offenders)}");
    }

    // ---- The rule on small sources: a guard that never fires would pass the repo too.

    [Theory]
    [InlineData("Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.Network, 0); Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("Titan.Domain.Aggregates.Vm.Vm.Create(); TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.Vm, 0);")]
    public void Rules_ACreatorThatSkipsTheGate_IsFound(string body)
    {
        Assert.Single(CreatorsThatSkipTheGate(CompileSample(body), []));
    }

    [Theory]
    [InlineData("TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.Vm, 0); Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.Vm, 0);")]
    [InlineData("Console.WriteLine();")]
    public void Rules_OtherMethods_AreNotFound(string body)
    {
        Assert.Empty(CreatorsThatSkipTheGate(CompileSample(body), []));
    }

    private static List<string> CreatorsThatSkipTheGate(CSharpCompilation compilation, HashSet<string> seen)
    {
        var offenders = new List<string>();
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (BaseMethodDeclarationSyntax method in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var asked = new HashSet<string>();
                foreach (InvocationExpressionSyntax call in method.DescendantNodes().OfType<InvocationExpressionSyntax>().OrderBy(c => c.SpanStart))
                {
                    if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol callee)
                    {
                        continue;
                    }
                    string name = $"{callee.ContainingType.Name}.{callee.Name}";
                    if (name == "TenantQuotaGate.EnsureHasRoom")
                    {
                        asked.Add(model.GetSymbolInfo(call.ArgumentList.Arguments[1].Expression).Symbol?.Name ?? "");
                    }
                    else if (name == "TenantQuotaGate.EnsureApiTokenRoom")
                    {
                        asked.Add("ApiToken");
                    }
                    else if (KindsEachCreatorNeeds.TryGetValue(name, out string[]? kinds)
                             && callee.ContainingNamespace.ToDisplayString().StartsWith("Titan.Domain.Aggregates", StringComparison.Ordinal))
                    {
                        seen.Add(name);
                        foreach (string kind in kinds.Where(k => !asked.Contains(k)))
                        {
                            FileLinePositionSpan span = call.GetLocation().GetLineSpan();
                            offenders.Add($"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1} {name} without {kind}");
                        }
                    }
                }
            }
        }
        return offenders;
    }

    internal static CSharpCompilation ApplicationCompilation()
    {
        var trees = new List<SyntaxTree>();
        foreach (string project in new[] { "Titan.Domain", ApplicationDir })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot.ControlPlane, project), "*.cs", SearchOption.AllDirectories))
            {
                bool isBuildOutput = file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                     || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");
                if (!isBuildOutput)
                {
                    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file));
                }
            }
        }
        Assert.True(trees.Count > FewestFilesInTheTwoProjects, $"Only {trees.Count} files.");

        CSharpCompilation compilation = Compile(trees);
        List<Diagnostic> errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != GeneratedRegexHasNoImplementation)
            .ToList();
        Assert.True(errors.Count == 0, $"The guard cannot read code that does not compile:\n{string.Join("\n", errors.Take(10))}");
        return compilation;
    }

    private static CSharpCompilation CompileSample(string body)
    {
        string source = "namespace Titan.Domain.Aggregates.Vm { public class Vm { public static Vm Create() { return new Vm(); } } } "
                        + "public enum TenantQuotaKind { Vm, Network } "
                        + "public static class TenantQuotaGate { public static void EnsureHasRoom(object tenant, TenantQuotaKind kind, int count) { } } "
                        + $"public class Handler {{ public void Handle(object tenant) {{ {body} }} }}";
        CSharpCompilation compilation = Compile([CSharpSyntaxTree.ParseText(source, path: "Sample.cs")]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    internal static CSharpCompilation Compile(List<SyntaxTree> trees)
    {
        var implicitUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
            + "global using System.Threading; global using System.Threading.Tasks;");
        List<PortableExecutableReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
        return CSharpCompilation.Create(
            "QuotaOwnership",
            [.. trees, implicitUsings],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    [Fact] // the guards above are only meaningful if the scan reaches the real application tree
    public void TheScanReachesEveryServiceModule()
    {
        // The service modules sit one level in, under Services/ (titan-ops architecture/code-structure.md#services-branch).
        var dirs = Directory
            .EnumerateDirectories(Path.Combine(RepoRoot.ControlPlane, ApplicationDir, "Services"))
            .Select(Path.GetFileName)
            .ToHashSet();

        foreach (var module in new[] { "Billing", "Catalog", "Compute", "Networking", "Platform" })
            Assert.Contains(module, dirs);
    }

    // The test above only asks "does a directory named Compute exist?", which cannot tell a module that is all
    // there from one that is half moved. This names a file from each branch: the scan has to reach the handlers.
    [Fact]
    public void TheScanReachesTheHandlerTree()
    {
        var files = ServiceFiles().Select(Path.GetFileName).ToHashSet();

        Assert.Contains("CreateVmCommandHandler.cs", files);   // Commands/Compute/Vm
        Assert.Contains("ListVmsQueryHandler.cs", files);      // Queries/Compute/Vm
        Assert.Contains("ComputeOwnership.cs", files);         // Services/Compute

        // And the branches are not mistaken for modules.
        Assert.DoesNotContain("Commands", ModuleDirs().Select(Path.GetFileName));
        Assert.DoesNotContain("Queries", ModuleDirs().Select(Path.GetFileName));
        Assert.DoesNotContain("Services", ModuleDirs().Select(Path.GetFileName));
    }
}
