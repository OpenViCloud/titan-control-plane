using System.Reflection;
using Titan.Application.Abstractions;

namespace Titan.Tests.Domain;

// titan-ops architecture/code-structure.md#query-handlers-cannot-write: a read path cannot write. These tests make that a
// MECHANISM instead of a naming convention: a handler under Queries/ that takes IProviderCommandQueue or
// IActivityRecorder fails here, and a developer who never reads this file still cannot write from a query,
// because the constructor has nowhere to put the tool.
//
// What these tests are SILENT about, said plainly because a green run invites the wrong conclusion:
//
//   - A query handler injected with a repository can still call repo.Add()/SaveAsync(). This guards the
//     CLEAREST edge, not every edge (risk-accepted); "the read path is incapable of writing" reads past
//     what is measured.
//   - They see constructor parameters. A handler that reaches a service locator, a static, or a
//     method argument is invisible here — the same blind spot ModuleBoundaryTests carries.
public class ReadWriteSeparationTests
{
    private static readonly Assembly ApplicationAssembly =
        typeof(Titan.Application.Queries.Compute.Vm.ListVmsQueryHandler).Assembly;

    private const string Queries = "Queries";
    private const string Commands = "Commands";
    private const string Services = "Services";

    // Commands/ and Queries/ are the FIRST level under Titan.Application (titan-ops architecture/code-structure.md#application-tree),
    // so the marker is the leading segment (Titan.Application.Queries.Compute.Vm), not a suffix. Every test below
    // asserts NotEmpty before its property, so a matcher that finds nothing fails loudly instead of passing.
    // The suffix says which side a handler is on (titan-ops architecture/code-structure.md#side-suffix): a type under
    // Commands/ that carries the query-side suffix is a naming error the compiler cannot see, and HandlerNames_CarryTheirSideSuffix fails on it.
    private static string SuffixFor(string branch) =>
        branch == Commands ? "CommandHandler" : "QueryHandler";

    private static List<Type> HandlersIn(string branch) =>
        ApplicationAssembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace is not null
                        && t.Namespace.StartsWith($"Titan.Application.{branch}.", StringComparison.Ordinal)
                        && t.Name.EndsWith("Handler", StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<Type> CtorParameterTypes(Type handler) =>
        handler.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);

    [Fact]
    public void QueryHandlers_DoNotInjectProviderCommandQueue()
    {
        var handlers = HandlersIn(Queries);

        // Zero handlers scanned would pass this test while measuring nothing. The count must be asserted
        // before the property is.
        Assert.NotEmpty(handlers);

        var offenders = handlers
            .Where(h => CtorParameterTypes(h).Any(t => t == typeof(IProviderCommandQueue)))
            .Select(h => h.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A handler in Queries/ injects IProviderCommandQueue. A read path that can enqueue a provider command is "
            + "a write path with a misleading address (docs/architecture/code-structure.md#query-handlers-cannot-write) — move the operation to Commands/: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void QueryHandlers_DoNotInjectActivityRecorder()
    {
        var handlers = HandlersIn(Queries);
        Assert.NotEmpty(handlers);

        var offenders = handlers
            .Where(h => CtorParameterTypes(h).Any(t => t == typeof(IActivityRecorder)))
            .Select(h => h.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A handler in Queries/ injects IActivityRecorder. Recording an activity is a write, and a "
            + "read path must not be able to make one (docs/architecture/code-structure.md#query-handlers-cannot-write): " + string.Join(", ", offenders));
    }

    [Fact] // one file, one job
    public void Handlers_ExposeExactlyOneHandleAsync()
    {
        var handlers = HandlersIn(Queries).Concat(HandlersIn(Commands)).ToList();
        Assert.NotEmpty(handlers);

        var offenders = new List<string>();
        foreach (var h in handlers)
        {
            var entryPoints = h
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "HandleAsync")
                .ToList();

            if (entryPoints.Count != 1)
                offenders.Add($"{h.Name}: {entryPoints.Count} public HandleAsync");
        }

        Assert.True(offenders.Count == 0,
            "A handler does not expose exactly one HandleAsync. The point of the split is that opening "
            + "one file shows one whole job (docs/architecture/code-structure.md#handler-shape): " + string.Join(" | ", offenders));
    }

    // A shared `internal static` on one handler is how command handlers end up depending on a query handler;
    // shared behaviour goes to Services/<Module>/ instead (titan-ops architecture/code-structure.md#handlers-do-not-call-handlers).
    [Fact]
    public void Handlers_DoNotReferenceOtherHandlers()
    {
        var handlers = HandlersIn(Queries).Concat(HandlersIn(Commands)).ToList();
        Assert.NotEmpty(handlers);

        var handlerTypes = handlers.ToHashSet();
        var offenders = new List<string>();

        foreach (var h in handlers)
        {
            foreach (var t in CtorParameterTypes(h).Where(handlerTypes.Contains))
                offenders.Add($"{h.Name} -> {t.Name}");

            foreach (var m in h.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Static | BindingFlags.Instance
                                           | BindingFlags.DeclaredOnly))
            {
                if (handlerTypes.Contains(m.ReturnType))
                    offenders.Add($"{h.Name}.{m.Name} returns {m.ReturnType.Name}");
                foreach (var p in m.GetParameters().Where(p => handlerTypes.Contains(p.ParameterType)))
                    offenders.Add($"{h.Name}.{m.Name} takes {p.ParameterType.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A handler depends on another handler. Shared work belongs on the aggregate or the "
            + "repository, not in a sibling handler — that is how the service this split took apart "
            + "grows back (docs/architecture/code-structure.md#handlers-do-not-call-handlers): " + string.Join(" | ", offenders));
    }

    // titan-ops architecture/code-structure.md#contracts — the suffix is the convention, and a convention with no guard is a comment.
    [Fact]
    public void HandlerNames_CarryTheirSideSuffix()
    {
        foreach (var branch in new[] { Commands, Queries })
        {
            var handlers = HandlersIn(branch);
            Assert.NotEmpty(handlers);

            var offenders = handlers
                .Where(h => !h.Name.EndsWith(SuffixFor(branch), StringComparison.Ordinal))
                .Select(h => $"{branch}/{h.Name} should end in {SuffixFor(branch)}")
                .ToList();

            Assert.True(offenders.Count == 0,
                "A handler's name does not say which side it is on (docs/architecture/code-structure.md#contracts): "
                + string.Join(" | ", offenders));
        }
    }

    // titan-ops architecture/code-structure.md#no-contract-reuse — neither side may return or accept the other's contracts.
    [Fact]
    public void NeitherSide_TouchesTheOthersContracts()
    {
        var offenders = new List<string>();

        foreach (var (branch, foreign) in new[] { (Commands, Queries), (Queries, Commands) })
        {
            var handlers = HandlersIn(branch);
            Assert.NotEmpty(handlers);

            var foreignNs = $"Titan.Application.{foreign}.";
            foreach (var h in handlers)
            {
                var touched = h.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetParameters().Select(x => x.ParameterType)
                        .Append(m.ReturnType))
                    .SelectMany(Unwrap)
                    .Where(t => t.Namespace?.StartsWith(foreignNs, StringComparison.Ordinal) == true)
                    .Select(t => t.Name)
                    .Distinct();

                foreach (var t in touched)
                    offenders.Add($"{h.Name} -> {foreign}.{t}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A handler on one side uses the other side's contract. Command contracts and query "
            + "contracts are deliberately separate types (docs/architecture/code-structure.md#contracts), even where the fields coincide: "
            + string.Join(" | ", offenders));
    }

    // Task<T> and List<T> hide the type that matters, so unwrap one level of each.
    private static IEnumerable<Type> Unwrap(Type t)
    {
        yield return t;
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                foreach (var inner in Unwrap(a))
                    yield return inner;
    }

    [Fact] // no junk drawer in the handler tree
    public void HandlerTree_HasNoHelperFiles()
    {
        var dirs = new[] { Commands, Queries }
            .Select(b => Path.Combine(RepoRoot.ControlPlane, "Titan.Application", b))
            .ToList();
        foreach (var d in dirs)
            Assert.True(Directory.Exists(d), $"The handler tree is not where this guard looks: {d}");

        var offenders = dirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Select(Path.GetFileName)
            .Where(f => f is not null
                        && (f.EndsWith("Helpers.cs", StringComparison.Ordinal)
                            || f.EndsWith("Common.cs", StringComparison.Ordinal)
                            || f.EndsWith("Shared.cs", StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A *Helpers/*Common/*Shared file appeared in the handler tree (docs/architecture/code-structure.md#handlers-do-not-call-handlers). Shared code "
            + "belongs on the aggregate or the repository: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ListVmsQueryHandler_TakesExactlyItsTwoDependencies()
    {
        var ctor = Assert.Single(typeof(Titan.Application.Queries.Compute.Vm.ListVmsQueryHandler).GetConstructors());
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Named, not counted: "2" alone would still pass if someone swapped one dependency for another.
        Assert.Equal(
            [typeof(Titan.Domain.Repositories.Compute.IVmRepository), typeof(ICurrentUser)],
            parameterTypes);
    }

    [Fact]
    public void HandlerNamespace_MatchesFolderPath()
    {
        var root = Path.Combine(RepoRoot.ControlPlane, "Titan.Application");
        var offenders = new List<string>();

        // Services/ follows the same folder-is-namespace rule as the two handler branches
        // (titan-ops architecture/code-structure.md#services-branch), so it is read here too rather than trusted.
        var branches = new[] { Commands, Queries, Services };
        foreach (var b in branches)
            Assert.True(Directory.Exists(Path.Combine(root, b)), $"Branch {b}/ is not where this guard looks.");

        var files = branches
            .SelectMany(b => Directory.EnumerateFiles(
                Path.Combine(root, b), "*.cs", SearchOption.AllDirectories))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var declared = File.ReadLines(file)
                .FirstOrDefault(l => l.StartsWith("namespace ", StringComparison.Ordinal))
                ?.Replace("namespace ", "").Replace(";", "").Trim();

            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
            var expected = "Titan.Application." + relative.Replace(Path.DirectorySeparatorChar, '.');

            if (declared != expected)
                offenders.Add($"{Path.GetFileName(file)}: declares {declared}, sits in {expected}");
        }

        Assert.True(offenders.Count == 0,
            "Namespace does not match folder path under Commands/Queries/Services: " + string.Join(" | ", offenders));
    }

    // titan-ops architecture/code-structure.md#services-branch: keeps a sixth kind of folder from growing back at the
    // root unnoticed; a new module-level service belongs in Services/<Module>/.
    //
    // Silent about: what sits INSIDE the five allowed folders. A service dropped into Abstractions/
    // passes here; that folder is a named non-module (QuotaOwnershipTests.NonModuleDirs, titan-ops architecture/code-structure.md#debt-and-accepted-risks).
    private static readonly string[] AllowedApplicationRootDirs =
        [Commands, Queries, Services, "Abstractions", "Options", "bin", "obj"];

    [Fact]
    public void ApplicationRoot_HoldsOnlyTheThreeBranchesAndTwoPortFolders()
    {
        var root = Path.Combine(RepoRoot.ControlPlane, "Titan.Application");
        var present = Directory.EnumerateDirectories(root)
            .Select(d => Path.GetFileName(d))
            .ToList();
        Assert.Contains(Commands, present); // proves the guard reads the real tree, not an empty one

        var unexpected = present
            .Where(name => !AllowedApplicationRootDirs.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var missing = new[] { Commands, Queries, Services }
            .Where(name => !present.Contains(name))
            .ToList();

        Assert.True(unexpected.Count == 0 && missing.Count == 0,
            "Titan.Application/ root must hold only Commands/, Queries/, Services/, Abstractions/ and "
            + "Options/ (docs/architecture/code-structure.md#services-branch). Move a module-level service into Services/<Module>/[<Aggregate>/]. "
            + $"Unexpected: [{string.Join(", ", unexpected)}]  Missing: [{string.Join(", ", missing)}]");
    }
}
