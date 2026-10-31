using System.Reflection;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Tests.Domain;

// titan-ops architecture/code-structure.md#domain-base-classes. The role of a domain type is its base class, so this
// guard reads it there: a list of roles kept in prose drifts from the code with no test failing.
public class DomainBaseClassTests
{
    private const string Aggregates = "Titan.Domain.Aggregates";
    private const string Values = "Titan.Domain.Values";

    // A record that is not a value object: it has no invariant of its own to keep, it only carries a call's
    // input or result. Each row says which call. A type that gains a role, or leaves the code, fails the test.
    private static readonly Dictionary<string, string> NoRole = new(StringComparer.Ordinal)
    {
        ["LoadBalancerListenerSpec"] = "input of LoadBalancer.Create, RequestUpdate and HasDesiredSpec; the listener it becomes is the child entity",
        ["VmIntentTicket"] = "result of a Vm intent call, read by the handler that enqueues the command",
        ["VolumeAttachProviderIds"] = "result of Volume attach, the provider ids the attach command carries",
        ["NetworkDeletionOutcome"] = "result of Network.MarkDeleted: the network's own outcome and the rules whose delete it finished",
        ["VolumeDeletionOutcome"] = "result of Volume.MarkDeleted: the volume's own outcome and the snapshots whose delete it finished",
        ["VmDeletionOutcome"] = "result of Vm.MarkDeleted: the intent it fulfilled and the snapshots whose delete it finished",
        ["ReconcilableNetwork"] = "one row of INetworkRepository.ListReconcilableAsync",
        ["ReconcilableFirewallRule"] = "one row of IFirewallRuleRepository.ListReconcilableAsync",
        ["ReconcilableVm"] = "one row of IVmRepository.ListReconcilableAsync",
        ["ReconcilableVmSnapshot"] = "one row of IVmSnapshotRepository.ListReconcilableAsync",
        ["ReconcilableVolume"] = "one row of IVolumeRepository.ListReconcilableAsync",
        ["ReconcilableVolumeSnapshot"] = "one row of IVolumeSnapshotRepository.ListReconcilableAsync",
    };

    [Fact]
    public void EveryDomainType_DeclaresItsRole()
    {
        var types = ScannedTypes().ToList();
        Assert.NotEmpty(types); // 0 offenders out of 0 types reads identical to a clean scan

        var offenders = types
            .Where(t => RoleOf(t) is null && !NoRole.ContainsKey(t.Name))
            .Select(t => $"{t.Name} in {t.Namespace}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Domain type(s) without a role. Derive from AggregateRoot, ChildEntity or ValueObject, or add a "
            + "row to NoRole saying which call the record carries: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RoleAllowlist_HasNoStaleRows()
    {
        var byName = ScannedTypes().ToDictionary(t => t.Name, StringComparer.Ordinal);

        var stale = new List<string>();
        foreach (var name in NoRole.Keys)
        {
            if (!byName.TryGetValue(name, out var type))
                stale.Add($"{name} (no such type)");
            else if (RoleOf(type) is not null)
                stale.Add($"{name} (now a {RoleOf(type)!.Name})");
        }

        Assert.True(stale.Count == 0, "NoRole row(s) to remove: " + string.Join(", ", stale));
    }

    [Fact]
    public void AggregateRoot_IsTheClassNamedLikeItsFolder()
    {
        var types = ScannedTypes().Where(t => t.Namespace!.StartsWith(Aggregates + ".", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(types);

        var offenders = new List<string>();
        foreach (var type in types)
        {
            var namedLikeFolder = type.Name == type.Namespace!.Split('.')[^1];
            var isRoot = typeof(AggregateRoot).IsAssignableFrom(type);
            if (namedLikeFolder && !isRoot)
                offenders.Add($"{type.Name} is named like its folder but is not an AggregateRoot");
            if (isRoot && !namedLikeFolder)
                offenders.Add($"{type.Name} is an AggregateRoot inside {type.Namespace}, whose root it is not");
        }

        Assert.True(offenders.Count == 0,
            "One aggregate root per aggregate folder, and it is the class named like the folder: "
            + string.Join(" | ", offenders.OrderBy(x => x, StringComparer.Ordinal)));
    }

    [Fact] // a public `set` or `init` lets `with` build a value its factory would refuse
    public void NoValueObject_HasAPubliclyWritableProperty()
    {
        var valueObjects = ScannedTypes().Where(t => typeof(ValueObject).IsAssignableFrom(t)).ToList();
        Assert.NotEmpty(valueObjects);

        var writable = valueObjects
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
                .Select(p => $"{t.Name}.{p.Name}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(writable.Count == 0,
            "Value object member(s) writable from outside, so `with` can skip the factory: "
            + string.Join(", ", writable));
    }

    // ---- machinery ---------------------------------------------------------------------------

    // Enums carry no role and a static class holds no state. A struct is scanned: it cannot derive from
    // ValueObject, so a value object written as a struct fails here instead of slipping past.
    private static IEnumerable<Type> ScannedTypes() =>
        typeof(NotFoundException).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace is not null
                        && (t.Namespace == Values || t.Namespace.StartsWith(Aggregates + ".", StringComparison.Ordinal))
                        && !t.IsEnum
                        && !t.IsInterface
                        && !(t.IsAbstract && t.IsSealed));

    private static Type? RoleOf(Type type)
    {
        foreach (var role in new[] { typeof(AggregateRoot), typeof(ChildEntity), typeof(ValueObject) })
            if (role.IsAssignableFrom(type))
                return role;
        return null;
    }
}
