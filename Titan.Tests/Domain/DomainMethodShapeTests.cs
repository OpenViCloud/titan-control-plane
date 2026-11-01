using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Titan.Tests.Domain;

// titan-ops architecture/domain-methods.md#domain-method-shape. Reads Titan.Domain through the compiler, not by text:
// whether a call lands on the member's own class, and how visible the target is, needs the semantic model.
public class DomainMethodShapeTests
{
    // The Domain has more files than this; a walk that finds fewer is looking in the wrong place.
    private const int FewestDomainFiles = 100;

    // Regex source generators do not run here, so a [GeneratedRegex] partial has no implementation part.
    private const string GeneratedRegexHasNoImplementation = "CS8795";

    private static readonly string[] GoneTestSeams = ["AddSnapshotUnchecked", "AddFirewallRuleUnchecked"];

    private static readonly string[] RootsWithoutASeam = ["Vm", "Volume", "Network", "SshKey", "ClusterIpv6Route"];

    [Fact]
    public void PublicMethods_DoNotCallPublicMethodsOfTheirOwnClass()
    {
        List<string> offenders = PublicCallingPublic(DomainCompilation());
        Assert.True(offenders.Count == 0, $"{offenders.Count} calls:\n{string.Join("\n", offenders)}");
    }

    [Fact]
    public void NoMember_OnlyForwardsToItsOwnClass()
    {
        List<string> offenders = HollowMembers(DomainCompilation());
        Assert.True(offenders.Count == 0, $"{offenders.Count} members:\n{string.Join("\n", offenders)}");
    }

    [Fact]
    public void TestSeams_AreGone()
    {
        string testsDir = Path.Combine(RepoRoot.ControlPlane, "Titan.Tests");
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            bool isBuildOutput = file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");
            bool isThisGuard = Path.GetFileName(file) == nameof(DomainMethodShapeTests) + ".cs";
            if (isBuildOutput || isThisGuard)
            {
                continue;
            }
            scanned++;
            string source = File.ReadAllText(file);
            foreach (string seam in GoneTestSeams)
            {
                if (source.Contains(seam + "("))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {seam}");
                }
            }
            foreach (string root in RootsWithoutASeam)
            {
                if (source.Contains($"{root}.CreateUnchecked("))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {root}.CreateUnchecked");
                }
            }
        }
        Assert.True(scanned > FewestDomainFiles, $"Only {scanned} test files under {testsDir}.");
        Assert.True(offenders.Count == 0, $"{offenders.Count} uses:\n{string.Join("\n", offenders)}");
    }

    // ---- The rules on small sources: a guard that never fires would pass the repo too.

    [Theory]
    [InlineData("public void A() { B(); } public void B() { Console.WriteLine(); }")]
    [InlineData("public void A() { this.B(); } public void B() { Console.WriteLine(); }")]
    [InlineData("public static void A() { Sample.B(); } public static void B() { Console.WriteLine(); }")]
    public void Rules_PublicCallingPublic_IsFound(string members)
    {
        Assert.Single(PublicCallingPublic(CompileSample(members)));
    }

    [Fact]
    public void Rules_PublicCallingAPublicMethodOfTheBaseClass_IsFound()
    {
        const string source = "public class Parent { public void B() { Console.WriteLine(); } } "
                              + "public class Sample : Parent { public void A() { B(); } }";
        Assert.Single(PublicCallingPublic(Compile(source)));
    }

    [Theory]
    [InlineData("public void A() { B(); } private void B() { Console.WriteLine(); }")]
    [InlineData("internal void A() { B(); } public void B() { Console.WriteLine(); }")]
    [InlineData("public void A(Sample other) { other.B(); } public void B() { Console.WriteLine(); }")]
    public void Rules_OtherCalls_AreNotPublicCallingPublic(string members)
    {
        Assert.Empty(PublicCallingPublic(CompileSample(members)));
    }

    [Fact]
    public void Rules_APublicMemberOfAPrivateNestedClass_IsNotPublic()
    {
        const string members = "private sealed class Reader { public void A() { B(); } public void B() { Console.WriteLine(); } }";
        Assert.Empty(PublicCallingPublic(CompileSample(members)));
    }

    [Theory]
    [InlineData("public void A() => B(1); private void B(int x) { Console.WriteLine(x); }")]
    [InlineData("public void A() { B(1); } private void B(int x) { Console.WriteLine(x); }")]
    [InlineData("public int A() { return B(1); } private int B(int x) { return x + 1; }")]
    [InlineData("internal void A() => B(1); private void B(int x) { Console.WriteLine(x); }")]
    [InlineData("private void A() => B(1); private void B(int x) { Console.WriteLine(x); }")]
    [InlineData("public int A => B(1); private int B(int x) { return x + 1; }")]
    [InlineData("public string A() => B() ?? throw new InvalidOperationException(); private string? B() { return Console.ReadLine(); }")]
    [InlineData("public int A() { C(); return B(1); } private int B(int x) { return x + 1; } private void C() { Console.WriteLine(); }")]
    [InlineData("public int A() { int y = B(1); C(); return B(y); } private int B(int x) { return x + 1; } private void C() { Console.WriteLine(); }")]
    public void Rules_AMemberThatOnlyForwardsToItsOwnClass_IsFound(string members)
    {
        Assert.Single(HollowMembers(CompileSample(members)));
    }

    [Theory]
    [InlineData("public int A() { return B(1) + 1; } private int B(int x) { return x + 1; }")]
    [InlineData("public bool A() { return B() && C(); } private bool B() { return Console.CapsLock; } private bool C() { return Console.NumberLock; }")]
    [InlineData("public int A() { if (Console.CapsLock) { return 1; } return B(1); } private int B(int x) { return x + 1; }")]
    [InlineData("public Sample A() { C(); return new Sample(); } private void C() { Console.WriteLine(); }")]
    [InlineData("private int _x; public void A() { _x = B(1); } private int B(int x) { return x + 1; }")]
    [InlineData("public int A(Child child) { return child.Run(); } public class Child { public int Run() { return Console.CursorLeft; } }")]
    [InlineData("public int A(Child child) { C(); return child.Run(); } private void C() { Console.WriteLine(); } public class Child { public int Run() { return Console.CursorLeft; } }")]
    [InlineData("public int A(Child child) { return Find(child).Run(); } private Child Find(Child c) { Console.WriteLine(); return c; } public class Child { public int Run() { return Console.CursorLeft; } }")]
    public void Rules_AMemberWithContentOfItsOwn_IsNotHollow(string members)
    {
        Assert.Empty(HollowMembers(CompileSample(members)));
    }

    // ---- The rules.

    private static List<string> PublicCallingPublic(CSharpCompilation compilation)
    {
        var offenders = new List<string>();
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (BaseMethodDeclarationSyntax member in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(member) is not IMethodSymbol caller || !IsEffectivelyPublic(caller))
                {
                    continue;
                }
                foreach (InvocationExpressionSyntax call in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (CalleeOfOwnClass(call, model, caller.ContainingType) is { } callee && IsEffectivelyPublic(callee))
                    {
                        offenders.Add($"{Where(call)} {caller.ContainingType.Name}.{caller.Name} -> {callee.Name}");
                    }
                }
            }
        }
        return offenders;
    }

    private static List<string> HollowMembers(CSharpCompilation compilation)
    {
        var offenders = new List<string>();
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (MemberDeclarationSyntax member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                INamedTypeSymbol? owner = model.GetDeclaredSymbol(member)?.ContainingType;
                if (owner != null && IsHollow(member, model, owner))
                {
                    offenders.Add($"{Where(member)} {owner.Name}.{model.GetDeclaredSymbol(member)!.Name}");
                }
            }
        }
        return offenders;
    }

    private static bool IsHollow(MemberDeclarationSyntax member, SemanticModel model, INamedTypeSymbol owner)
    {
        if (member is ConstructorDeclarationSyntax { Initializer: { } initializer, Body.Statements.Count: 0 }
            && initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
        {
            return true;
        }

        List<ExpressionSyntax>? calls = OnlyCalls(member);
        if (calls == null || calls.Count == 0)
        {
            return false;
        }
        return calls[^1] is InvocationExpressionSyntax last && CalleeOfOwnClass(last, model, owner) != null;
    }

    // The expressions a body is made of, when it is made of nothing but calls; null when it does anything else.
    private static List<ExpressionSyntax>? OnlyCalls(MemberDeclarationSyntax member)
    {
        ArrowExpressionClauseSyntax? arrow = null;
        BlockSyntax? block = null;
        switch (member)
        {
            case BaseMethodDeclarationSyntax method:
                arrow = method.ExpressionBody;
                block = method.Body;
                break;
            case PropertyDeclarationSyntax property:
                arrow = property.ExpressionBody;
                AccessorDeclarationSyntax? getter = property.AccessorList?.Accessors
                    .FirstOrDefault(a => a.Keyword.IsKind(SyntaxKind.GetKeyword));
                if (getter != null)
                {
                    arrow ??= getter.ExpressionBody;
                    block = getter.Body;
                }
                break;
            default:
                return null;
        }

        if (arrow != null)
        {
            return CallOf(arrow.Expression) is { } call ? [call] : null;
        }
        if (block == null)
        {
            return null;
        }

        var calls = new List<ExpressionSyntax>();
        foreach (StatementSyntax statement in block.Statements)
        {
            switch (statement)
            {
                case ExpressionStatementSyntax expression when CallOf(expression.Expression) is { } call:
                    calls.Add(call);
                    break;
                case ReturnStatementSyntax { Expression: IdentifierNameSyntax }:
                    break;
                case ReturnStatementSyntax { Expression: { } returned } when CallOf(returned) is { } call:
                    calls.Add(call);
                    break;
                case LocalDeclarationStatementSyntax local when local.Declaration.Variables.Count == 1
                                                                && local.Declaration.Variables[0].Initializer?.Value is { } value
                                                                && CallOf(value) is { } call:
                    calls.Add(call);
                    break;
                default:
                    return null;
            }
        }
        return calls;
    }

    // `Call()` and `Call() ?? throw …`; anything else is an expression with content of its own.
    private static ExpressionSyntax? CallOf(ExpressionSyntax expression)
    {
        if (expression is AwaitExpressionSyntax awaited)
        {
            return CallOf(awaited.Expression);
        }
        if (expression is InvocationExpressionSyntax)
        {
            return expression;
        }
        bool callOrThrow = expression is BinaryExpressionSyntax { Left: InvocationExpressionSyntax, Right: ThrowExpressionSyntax }
                           && expression.IsKind(SyntaxKind.CoalesceExpression);
        return callOrThrow ? ((BinaryExpressionSyntax)expression).Left : null;
    }

    // The target of a call on the member's own class or a base: no receiver, this, base, or the class's own name.
    private static IMethodSymbol? CalleeOfOwnClass(InvocationExpressionSyntax call, SemanticModel model, INamedTypeSymbol owner)
    {
        if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol callee || callee.MethodKind != MethodKind.Ordinary)
        {
            return null;
        }
        bool isOwnClass = false;
        for (INamedTypeSymbol? type = owner; type != null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, callee.ContainingType.OriginalDefinition))
            {
                isOwnClass = true;
            }
        }
        if (!isOwnClass)
        {
            return null;
        }
        switch (call.Expression)
        {
            case SimpleNameSyntax:
                return callee;
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax }:
                return callee;
            case MemberAccessExpressionSyntax access when callee.IsStatic
                                                          && SymbolEqualityComparer.Default.Equals(
                                                              model.GetSymbolInfo(access.Expression).Symbol, owner):
                return callee;
            default:
                return null;
        }
    }

    private static bool IsEffectivelyPublic(ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }
        for (INamedTypeSymbol? type = symbol.ContainingType; type != null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }
        return true;
    }

    private static string Where(SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    // ---- Compiling.

    private static CSharpCompilation DomainCompilation()
    {
        string domainDir = Path.Combine(RepoRoot.ControlPlane, "Titan.Domain");
        var trees = new List<SyntaxTree>();
        foreach (string file in Directory.EnumerateFiles(domainDir, "*.cs", SearchOption.AllDirectories))
        {
            bool isBuildOutput = file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");
            if (!isBuildOutput)
            {
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file));
            }
        }
        Assert.True(trees.Count > FewestDomainFiles, $"Only {trees.Count} files under {domainDir}.");

        CSharpCompilation compilation = Compile(trees);
        List<Diagnostic> errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != GeneratedRegexHasNoImplementation)
            .ToList();
        Assert.True(errors.Count == 0, $"The guard cannot read a Domain that does not compile:\n{string.Join("\n", errors.Take(10))}");
        return compilation;
    }

    private static CSharpCompilation CompileSample(string members) =>
        Compile($"public class Sample {{ {members} }}");

    private static CSharpCompilation Compile(string source)
    {
        CSharpCompilation compilation = Compile([CSharpSyntaxTree.ParseText(source, path: "Sample.cs")]);
        List<Diagnostic> errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, $"A sample must compile:\n{string.Join("\n", errors)}");
        return compilation;
    }

    private static CSharpCompilation Compile(List<SyntaxTree> trees)
    {
        var implicitUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
            + "global using System.Threading; global using System.Threading.Tasks;");
        List<PortableExecutableReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
        return CSharpCompilation.Create(
            "DomainMethodShape",
            [.. trees, implicitUsings],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
