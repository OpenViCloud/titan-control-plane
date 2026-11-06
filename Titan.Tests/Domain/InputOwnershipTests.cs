using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Titan.Tests.Domain;

// The name and size a customer sends are checked by the command handler, before the aggregate is built or renamed
// (titan-ops architecture/domain-methods.md#names-and-sizes). The aggregate takes what it is given, so a handler that
// forgot the check would pass every test of a different handler: this reads the methods of Titan.Application through the
// compiler, as QuotaOwnershipTests does for the quota gate.
//
// What it does NOT see: a check inside a branch counts as asked; a creator in Titan.Infrastructure; a check on another
// value than the one passed on (it matches the call, not the argument).
public class InputOwnershipTests
{
    // A creator or rename adds a row here, with the checks its input needs asked before it.
    private static readonly Dictionary<string, string[]> ChecksEachCreatorNeeds = new()
    {
        ["Vm.Create"] = ["EnsureVmName", "EnsureDiskSize"],
        ["Vm.Rename"] = ["EnsureVmName"],
        ["Volume.Create"] = ["EnsureVolumeName", "EnsureVolumeSize"],
        ["LoadBalancer.Create"] = ["EnsureLoadBalancerName"],
        ["LoadBalancer.Rename"] = ["EnsureLoadBalancerName"],
        ["Vm.AddSnapshot"] = ["EnsureVmSnapshotName"],
        ["Volume.AddSnapshot"] = ["EnsureVolumeSnapshotName"],
    };

    [Fact]
    public void EveryCreator_ChecksTheInputFirst()
    {
        var seen = new HashSet<string>();
        List<string> offenders = CreatorsThatSkipTheCheck(QuotaOwnershipTests.ApplicationCompilation(), seen);

        Assert.True(seen.SetEquals(ChecksEachCreatorNeeds.Keys),
            $"Calls seen: {string.Join(", ", seen.Order())}. A renamed member would otherwise pass by finding nothing.");
        Assert.True(offenders.Count == 0, $"{offenders.Count} calls:\n{string.Join("\n", offenders)}");
    }

    // ---- The rule on small sources: a guard that never fires would pass the repo too.

    [Theory]
    [InlineData("Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("ComputeInput.EnsureVmName(); Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("Titan.Domain.Aggregates.Vm.Vm.Create(); ComputeInput.EnsureVmName(); ComputeInput.EnsureDiskSize();")]
    [InlineData("ComputeInput.EnsureDiskSize(); Titan.Domain.Aggregates.Vm.Vm.Create();")]
    public void Rules_ACreatorThatSkipsACheck_IsFound(string body)
    {
        Assert.NotEmpty(CreatorsThatSkipTheCheck(CompileSample(body), []));
    }

    [Theory]
    [InlineData("ComputeInput.EnsureVmName(); ComputeInput.EnsureDiskSize(); Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("ComputeInput.EnsureDiskSize(); ComputeInput.EnsureVmName(); Titan.Domain.Aggregates.Vm.Vm.Create();")]
    [InlineData("ComputeInput.EnsureVmName();")]
    [InlineData("Console.WriteLine();")]
    public void Rules_OtherMethods_AreNotFound(string body)
    {
        Assert.Empty(CreatorsThatSkipTheCheck(CompileSample(body), []));
    }

    private static List<string> CreatorsThatSkipTheCheck(CSharpCompilation compilation, HashSet<string> seen)
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
                    if (callee.ContainingType.Name == "ComputeInput")
                    {
                        asked.Add(callee.Name);
                    }
                    else if (ChecksEachCreatorNeeds.TryGetValue(name, out string[]? checks)
                             && callee.ContainingNamespace.ToDisplayString().StartsWith("Titan.Domain.Aggregates", StringComparison.Ordinal))
                    {
                        seen.Add(name);
                        foreach (string check in checks.Where(c => !asked.Contains(c)))
                        {
                            FileLinePositionSpan span = call.GetLocation().GetLineSpan();
                            offenders.Add($"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1} {name} without {check}");
                        }
                    }
                }
            }
        }
        return offenders;
    }

    private static CSharpCompilation CompileSample(string body)
    {
        string source = "namespace Titan.Domain.Aggregates.Vm { public class Vm { public static Vm Create() { return new Vm(); } } } "
                        + "public static class ComputeInput { public static void EnsureVmName() { } public static void EnsureDiskSize() { } } "
                        + $"public class Handler {{ public void Handle() {{ {body} }} }}";
        CSharpCompilation compilation = QuotaOwnershipTests.Compile([CSharpSyntaxTree.ParseText(source, path: "Sample.cs")]);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }
}
