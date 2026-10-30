using System.Reflection;
using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Tests.Domain;

// titan-ops architecture/code-structure.md#child-entity: the parent writes its children, and nothing else does.
//
// What these tests are SILENT about: they read signatures. A repository method with an innocent name and shape that
// writes a child through its DbContext is invisible here, and so is a handler that loads two aggregates and saves them
// together. Green means "no write path in the shapes we look for", not "no path exists".
public class ChildEntityGuardTests
{
    private static readonly Assembly DomainAssembly = typeof(NotFoundException).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ICurrentUser).Assembly;

    private const string RepositoriesNamespace = "Titan.Domain.Repositories";

    private static List<Type> RepositoryInterfaces() =>
        DomainAssembly.GetExportedTypes()
            .Where(t => t.IsInterface && t.Namespace == RepositoriesNamespace)
            .ToList();

    private static List<(Type Repository, MethodInfo Method)> RepositoryMethods() =>
        RepositoryInterfaces()
            .SelectMany(r => r.GetMethods().Select(m => (r, m)))
            .ToList();

    private static IEnumerable<Type> ItselfAndGenericArguments(Type type)
    {
        yield return type;
        if (type.IsArray)
        {
            foreach (Type inner in ItselfAndGenericArguments(type.GetElementType()!))
            {
                yield return inner;
            }
        }
        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type inner in ItselfAndGenericArguments(argument))
            {
                yield return inner;
            }
        }
    }

    private static bool MentionsAChild(Type type) =>
        ItselfAndGenericArguments(type).Any(t => typeof(ChildEntity).IsAssignableFrom(t));

    // A *ForUpdateAsync loads one root for writing: one that hands out children is the aggregate boundary done by hand.
    [Fact]
    public void ForUpdateMethods_LoadOneAggregateRoot()
    {
        var forUpdate = RepositoryMethods().Where(x => x.Method.Name.EndsWith("ForUpdateAsync", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(forUpdate); // 0 offenders out of 0 methods reads identical to a clean scan

        var offenders = forUpdate
            .Where(x =>
            {
                Type returned = x.Method.ReturnType;
                bool isTaskOfOne = returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(Task<>);
                return !isTaskOfOne || !typeof(AggregateRoot).IsAssignableFrom(returned.GetGenericArguments()[0]);
            })
            .Select(x => $"{x.Repository.Name}.{x.Method.Name} returns {x.Method.ReturnType}")
            .ToList();

        Assert.True(offenders.Count == 0, "A *ForUpdateAsync must load one aggregate root: " + string.Join(", ", offenders));
    }

    // A bulk write past the aggregate ends a child's request without its outcome.
    [Fact]
    public void NoRepository_HasAMarkAllMethod()
    {
        var methods = RepositoryMethods();
        Assert.NotEmpty(methods);

        var offenders = methods
            .Where(x => x.Method.Name.StartsWith("MarkAll", StringComparison.Ordinal))
            .Select(x => $"{x.Repository.Name}.{x.Method.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "Write through the root, not in bulk: " + string.Join(", ", offenders));
    }

    // Structural half: a child goes into a repository only inside its root.
    private static readonly Dictionary<string, string> RepositoryMethodsTakingAChild = new(StringComparer.Ordinal)
    {
        ["IWalletRepository.AddTransaction"] =
            "the ledger grows without bound, so the wallet does not load it; the row commits in the balance's SaveChanges",
    };

    [Fact]
    public void NoRepository_TakesAChildEntity_BeyondTheAllowlist()
    {
        var methods = RepositoryMethods();
        Assert.NotEmpty(methods);

        var found = methods
            .Where(x => x.Method.GetParameters().Any(p => MentionsAChild(p.ParameterType)))
            .Select(x => $"{x.Repository.Name}.{x.Method.Name}")
            .ToList();

        var undeclared = found.Where(x => !RepositoryMethodsTakingAChild.ContainsKey(x)).ToList();
        var stale = RepositoryMethodsTakingAChild.Keys.Where(x => !found.Contains(x)).ToList();
        Assert.True(undeclared.Count == 0 && stale.Count == 0,
            "A repository takes the root, never a child.\n  undeclared: " + string.Join(", ", undeclared)
            + "\n  stale: " + string.Join(", ", stale));
    }

    // Structural half: a child's own repository is the read side (lists, the meter, the reconciler): every method hands
    // back children. Found by name, so a method returning a root cannot take the repository out of the scan.
    [Fact]
    public void AChildsOwnRepository_OnlyHandsOutChildren()
    {
        var childNames = DomainAssembly.GetExportedTypes()
            .Where(t => typeof(ChildEntity).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => $"I{t.Name}Repository")
            .ToHashSet(StringComparer.Ordinal);
        var childRepositories = RepositoryInterfaces().Where(r => childNames.Contains(r.Name)).ToList();
        Assert.Equal(3, childRepositories.Count); // FirewallRule, VolumeSnapshot, VmSnapshot

        var offenders = childRepositories
            .SelectMany(r => r.GetMethods().Select(m => (r, m)))
            .Where(x => !MentionsAChild(x.m.ReturnType))
            .Select(x => $"{x.r.Name}.{x.m.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "A child's own repository only hands out its rows: " + string.Join(", ", offenders));
    }

    // Structural half: the compiler keeps Titan.Application off a child's writes, since they are internal. A public
    // method on a child is a way around the root, so each one says why it is not.
    private static readonly Dictionary<string, string> PublicChildMethods = new(StringComparer.Ordinal)
    {
        ["FirewallRule.RequireExternalId"] = "a read: hands the provider id to the reconciler's read model",
        ["VolumeSnapshot.RequireExternalId"] = "a read: hands the provider id to the reconciler's read model",
        ["VmSnapshot.RequireExternalId"] = "a read: hands the provider id to the reconciler's read model",
        ["InvoiceItem.Create"] = "the items exist before their invoice: built first, handed to the invoice's factory",
        ["WalletTransaction.ForTopUp"] = "the ledger is not loaded with the wallet: the factory applies the sign, the row goes in beside the balance",
        ["WalletTransaction.ForPromo"] = "same ledger row",
        ["WalletTransaction.ForInvoicePayment"] = "same ledger row",
    };

    [Fact]
    public void ChildEntities_ExposeNoPublicMethodBeyondTheAllowlist()
    {
        var children = DomainAssembly.GetExportedTypes().Where(t => typeof(ChildEntity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(children);

        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = children
            .SelectMany(c => c.GetMethods(surface).Where(m => !m.IsSpecialName).Select(m => $"{c.Name}.{m.Name}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var undeclared = found.Where(x => !PublicChildMethods.ContainsKey(x)).ToList();
        var stale = PublicChildMethods.Keys.Where(x => !found.Contains(x)).ToList();
        Assert.True(undeclared.Count == 0 && stale.Count == 0,
            "Public method on a child: go through the root, or add a row saying why.\n  undeclared: "
            + string.Join(", ", undeclared) + "\n  stale: " + string.Join(", ", stale));
    }

    // A setter, a field or a public constructor is the same way around the root.
    [Fact]
    public void ChildEntities_HaveNoPublicSetterFieldOrConstructor()
    {
        var children = DomainAssembly.GetExportedTypes().Where(t => typeof(ChildEntity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(children);

        var offenders = new List<string>();
        foreach (Type child in children)
        {
            offenders.AddRange(child.GetProperties().Where(p => p.SetMethod is { IsPublic: true }).Select(p => $"{child.Name}.{p.Name} setter"));
            offenders.AddRange(child.GetFields().Select(f => $"{child.Name}.{f.Name} field"));
            offenders.AddRange(child.GetConstructors().Select(_ => $"{child.Name} constructor"));
        }

        Assert.True(offenders.Count == 0, "Public way around the root: " + string.Join(", ", offenders));
    }

    // titan-ops architecture/code-structure.md#application-tree: the third segment of the Application tree is an aggregate;
    // each folder that is not says why.
    private static readonly Dictionary<string, string> ThirdSegmentExceptions = new(StringComparer.Ordinal)
    {
        ["Legal"] = "the aggregate is Acceptance; the name matches Endpoints/Legal",
        ["Cluster"] = "no aggregate: a cluster is config",
        ["OsImage"] = "no aggregate: read live from Incus",
        ["Metering"] = "no aggregate: the background meters",
        ["Backup"] = "no aggregate of this name: BackupRecord and BackupObservation, and the VMs and volumes it reads",
        ["BackupTarget"] = "no aggregate: the bucket is config",
        ["WalletMutations"] = "the job over WalletMutation: a folder of the same name would hide the type (CS0118)",
        ["FirewallRule"] = "a child with its own routes and callback; the handlers load and save its Network",
        ["VolumeSnapshot"] = "a child with its own routes and callback; the handlers load and save its Volume",
        ["VmSnapshot"] = "a child with its own routes and callback; the handlers load and save its Vm",
    };

    [Fact]
    public void ApplicationThirdSegment_IsAnAggregate_OrANamedException()
    {
        var aggregates = DomainAssembly.GetExportedTypes()
            .Where(t => typeof(AggregateRoot).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(aggregates);

        var segments = ApplicationAssembly.GetTypes()
            .Select(t => t.Namespace ?? "")
            .Select(n => n.Split('.'))
            .Where(p => p.Length >= 5 && p[0] == "Titan" && p[1] == "Application" && p[2] is "Commands" or "Queries" or "Services")
            .Select(p => p[4])
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(segments.Count >= 20, $"Scan saw only {segments.Count} folders");

        var undeclared = segments.Where(s => !aggregates.Contains(s) && !ThirdSegmentExceptions.ContainsKey(s)).ToList();
        var stale = ThirdSegmentExceptions.Keys.Where(s => !segments.Contains(s) || aggregates.Contains(s)).ToList();
        Assert.True(undeclared.Count == 0 && stale.Count == 0,
            "Folder under Titan.Application that is not an aggregate: add a row saying why.\n  undeclared: "
            + string.Join(", ", undeclared) + "\n  stale: " + string.Join(", ", stale));
    }
}
