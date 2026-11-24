using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Titan.Tests.Domain;

// Vm.RestoreFromBackup and Volume.RestoreFromBackup build a row that never counts toward quota and is never billed
// (titan-ops features/backup/design.md#restore-as-new-resource), and neither asks the quota gate: a tenant's own path
// that reached one would hand out free resources. Who calls a method is not observable from behaviour, so this reads
// Titan.Domain and Titan.Application through the compiler, as QuotaOwnershipTests does, and names the only callers: the
// two admin restore handlers.
//
// What it does NOT see, stated because a green run reads like "nobody else can":
//   - a caller in Titan.Infrastructure or Titan.Host: only Titan.Application and Titan.Domain are read
//   - a call made through reflection, or a row whose flag is written behind the factories (the setter is private)
//   - who may reach the two handlers: that is the endpoints' role check
public class RestoreFromBackupCallerTests
{
    private const string VmHandler = "Titan.Application/Commands/Admin/Backup/RestoreVmFromBackupCommandHandler.cs";
    private const string VolumeHandler = "Titan.Application/Commands/Admin/Backup/RestoreVolumeFromBackupCommandHandler.cs";

    // Every reference there is, as "<file> -> <factory>": restoring a VM restores the volumes that go with it, so that
    // handler builds both. A new caller is a decision about who gets free resources: it adds a row here.
    private static readonly string[] TheOnlyCallers =
    [
        $"{VmHandler} -> Vm.RestoreFromBackup",
        $"{VmHandler} -> Volume.RestoreFromBackup",
        $"{VolumeHandler} -> Volume.RestoreFromBackup",
    ];

    [Fact]
    public void OnlyTheAdminRestoreHandlers_CallTheRestoreFactories()
    {
        List<string> references = ReferencesToTheFactories(QuotaOwnershipTests.ApplicationCompilation(), RepoRoot.ControlPlane);

        List<string> elsewhere = references.Where(r => !TheOnlyCallers.Contains(r)).ToList();
        List<string> unseen = TheOnlyCallers.Where(c => !references.Contains(c)).ToList();
        Assert.True(elsewhere.Count == 0,
            "A restore factory is called outside the admin restore handlers. It builds a VM or volume that is free, "
            + "without asking the quota gate (titan-ops features/backup/design.md#restore-as-new-resource):\n"
            + string.Join("\n", elsewhere));
        Assert.True(unseen.Count == 0,
            "The guard no longer sees a call it names; a moved or renamed handler or factory would otherwise pass by "
            + $"finding nothing:\n{string.Join("\n", unseen)}\nReferences seen:\n{string.Join("\n", references)}");
    }

    // ---- The rule on small sources: a guard that never fires would pass the repo too.

    [Theory]
    [InlineData("Titan.Domain.Aggregates.Vm.Vm.RestoreFromBackup();", "Vm.RestoreFromBackup")]
    [InlineData("Titan.Domain.Aggregates.Volume.Volume.RestoreFromBackup();", "Volume.RestoreFromBackup")]
    [InlineData("var made = Titan.Domain.Aggregates.Vm.Vm.RestoreFromBackup();", "Vm.RestoreFromBackup")]
    // Handed on as a delegate, not called here: still a way to the factory.
    [InlineData("Func<Titan.Domain.Aggregates.Vm.Vm> make = Titan.Domain.Aggregates.Vm.Vm.RestoreFromBackup;", "Vm.RestoreFromBackup")]
    public void Rules_AReferenceToAFactory_IsFound_WithItsFile(string body, string factory)
    {
        Assert.Equal([$"Titan.Application/Commands/Compute/Vm/Sample.cs -> {factory}"], ReferencesToTheFactories(CompileSample(body), SampleRoot));
    }

    [Fact]
    public void Rules_AReferenceOutsideAMethodBody_IsFound()
    {
        const string source = SampleDomain
                              + "public class Handler { private readonly object _made = Titan.Domain.Aggregates.Volume.Volume.RestoreFromBackup(); }";

        Assert.Equal(
            ["Titan.Application/Commands/Compute/Vm/Sample.cs -> Volume.RestoreFromBackup"],
            ReferencesToTheFactories(Compile(source), SampleRoot));
    }

    [Theory]
    [InlineData("Titan.Domain.Aggregates.Vm.Vm.Create();")]
    // Another type's method of the same name is not a restore factory.
    [InlineData("Other.RestoreFromBackup();")]
    [InlineData("Console.WriteLine();")]
    public void Rules_OtherCalls_AreNotFound(string body)
    {
        Assert.Empty(ReferencesToTheFactories(CompileSample(body), SampleRoot));
    }

    // ---- The rule.

    private static readonly string[] Factories = ["Vm.RestoreFromBackup", "Volume.RestoreFromBackup"];

    // Every mention of a restore factory outside its declaration, a call or a method group handed on, as
    // "<file relative to root> -> <Type>.RestoreFromBackup".
    private static List<string> ReferencesToTheFactories(CSharpCompilation compilation, string root)
    {
        var references = new List<string>();
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (name.Identifier.ValueText != "RestoreFromBackup")
                {
                    continue;
                }
                SymbolInfo info = model.GetSymbolInfo(name);
                ISymbol? symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is not IMethodSymbol method
                    || !method.ContainingNamespace.ToDisplayString().StartsWith("Titan.Domain.Aggregates", StringComparison.Ordinal))
                {
                    continue;
                }
                string factory = $"{method.ContainingType.Name}.{method.Name}";
                if (!Factories.Contains(factory))
                {
                    continue;
                }
                string file = Path.GetRelativePath(root, tree.FilePath).Replace(Path.DirectorySeparatorChar, '/');
                string reference = $"{file} -> {factory}";
                if (!references.Contains(reference))
                {
                    references.Add(reference);
                }
            }
        }
        return references;
    }

    // ---- Compiling the samples.

    private static readonly string SampleRoot = Path.Combine(Path.GetTempPath(), "restore-from-backup-caller-sample");

    private const string SampleDomain =
        "namespace Titan.Domain.Aggregates.Vm { public class Vm { public static Vm Create() { return new Vm(); } "
        + "public static Vm RestoreFromBackup() { return new Vm(); } } } "
        + "namespace Titan.Domain.Aggregates.Volume { public class Volume { public static Volume RestoreFromBackup() { return new Volume(); } } } "
        + "public static class Other { public static void RestoreFromBackup() { } } ";

    private static CSharpCompilation CompileSample(string body) =>
        Compile(SampleDomain + $"public class Handler {{ public void Handle() {{ {body} }} }}");

    private static CSharpCompilation Compile(string source)
    {
        string path = Path.Combine(SampleRoot, "Titan.Application", "Commands", "Compute", "Vm", "Sample.cs");
        CSharpCompilation compilation = QuotaOwnershipTests.Compile([CSharpSyntaxTree.ParseText(source, path: path)]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }
}
