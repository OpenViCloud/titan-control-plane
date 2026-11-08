using System.Reflection;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// The pending operation and the last failure live once in ProviderResource (titan-ops
// architecture/domain-methods.md#provider-resource), for roots and entities alike. Every Domain type that has a Pending
// operation goes through it, so the state is never written again in a type of its own. Vm, Network, FirewallRule,
// VmSnapshot, Volume, VolumeSnapshot and VmTemplateCluster are the named exceptions: each keeps one status that says
// which operation waits, and keeps that operation's id and the last failure in an Operation value object of its own
// instead of a Pending (titan-ops features/vm/design.md#vm-status, features/network/design.md#network-operation,
// features/vm/design.md#vm-snapshot, features/volume/design.md#volume-state-is-existence,
// features/catalog/design.md#template-state-is-existence).
public class ProviderResourceTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static HashSet<string> BaseNames() =>
        typeof(ProviderResource<>).GetMembers(Declared)
            .Where(m => !m.Name.StartsWith('<') && m is not ConstructorInfo)
            .Select(m => m.Name)
            .ToHashSet();

    [Fact]
    public void EveryTypeWithAPendingOperation_GetsItFromProviderResource()
    {
        List<Type> withPending = typeof(ProviderResource<>).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetProperty("Pending")?.PropertyType is { IsGenericType: true } p
                        && p.GetGenericTypeDefinition() == typeof(PendingOperation<>))
            .ToList();
        Assert.True(withPending.Count >= 1, $"the scan found only {withPending.Count} types with a pending operation");
        // Vm, Network, FirewallRule, VmSnapshot, Volume, VolumeSnapshot and VmTemplateCluster have no Pending, so this scan does not reach them: the tests below hold them.
        Assert.DoesNotContain(typeof(Vm), withPending);
        Assert.DoesNotContain(typeof(Network), withPending);
        Assert.DoesNotContain(typeof(FirewallRule), withPending);
        Assert.DoesNotContain(typeof(VmSnapshot), withPending);
        Assert.DoesNotContain(typeof(Volume), withPending);
        Assert.DoesNotContain(typeof(VolumeSnapshot), withPending);
        Assert.DoesNotContain(typeof(VmTemplateCluster), withPending);

        HashSet<string> baseNames = BaseNames();
        List<string> declaringItThemselves = withPending
            .SelectMany(t => t.GetMembers(Declared).Where(m => baseNames.Contains(m.Name)).Select(m => t.Name + "." + m.Name))
            .ToList();
        Assert.True(declaringItThemselves.Count == 0, "Declares the base's state again: " + string.Join(", ", declaringItThemselves));
    }

    // Outside ProviderResource, no type writes the request or failure state again: Vm keeps its operation, its last failure
    // and the Incus status seen in one VmOperation under other names, and a type declaring the base's names is a copy of the
    // base instead of deriving from it.
    [Fact]
    public void Vm_KeepsItsOperationInASingleVmOperation_AndNoTypeCopiesTheBasesState()
    {
        Assert.False(DerivesFromProviderResource(typeof(Vm)));
        Assert.Null(typeof(Vm).GetProperty("Pending"));
        Assert.Equal(typeof(VmOperation), typeof(Vm).GetProperty("Operation")?.PropertyType);

        HashSet<string> baseNames = BaseNames();
        List<string> keepingItThemselves = typeof(ProviderResource<>).Assembly.GetTypes()
            .Where(t => t.IsClass && t != typeof(ProviderResource<>) && !DerivesFromProviderResource(t))
            .Where(t => t.GetMembers(Declared).Any(m => baseNames.Contains(m.Name)))
            .Select(t => t.Name)
            .ToList();
        Assert.Empty(keepingItThemselves);
    }

    [Fact]
    public void NetworkAndFirewallRule_KeepTheirOperationInAnOperationValueObject_AndAreNotOnTheBase()
    {
        Assert.False(DerivesFromProviderResource(typeof(Network)));
        Assert.False(DerivesFromProviderResource(typeof(FirewallRule)));
        Assert.Null(typeof(Network).GetProperty("Pending"));
        Assert.Null(typeof(FirewallRule).GetProperty("Pending"));
        Assert.Equal(typeof(NetworkOperation), typeof(Network).GetProperty("Operation")?.PropertyType);
        Assert.Equal(typeof(FirewallRuleOperation), typeof(FirewallRule).GetProperty("Operation")?.PropertyType);
    }

    [Fact]
    public void VmSnapshot_KeepsItsOperationInAVmSnapshotOperation_AndIsNotOnTheBase()
    {
        Assert.False(DerivesFromProviderResource(typeof(VmSnapshot)));
        Assert.Null(typeof(VmSnapshot).GetProperty("Pending"));
        PropertyInfo? operation = typeof(VmSnapshot).GetProperty("Operation");
        Assert.NotNull(operation);
        Assert.Equal(typeof(VmSnapshotOperation), operation.PropertyType);
    }

    [Fact]
    public void VolumeAndVolumeSnapshot_KeepTheirOperationInAnOperationValueObject_AndAreNotOnTheBase()
    {
        Assert.False(DerivesFromProviderResource(typeof(Volume)));
        Assert.False(DerivesFromProviderResource(typeof(VolumeSnapshot)));
        Assert.Null(typeof(Volume).GetProperty("Pending"));
        Assert.Null(typeof(VolumeSnapshot).GetProperty("Pending"));
        Assert.Equal(typeof(VolumeOperation), typeof(Volume).GetProperty("Operation")?.PropertyType);
        Assert.Equal(typeof(VolumeSnapshotOperation), typeof(VolumeSnapshot).GetProperty("Operation")?.PropertyType);
    }

    [Fact]
    public void VmTemplateCluster_KeepsItsOperationInAVmTemplateOperation_AndIsNotOnTheBase()
    {
        Assert.False(DerivesFromProviderResource(typeof(VmTemplateCluster)));
        Assert.Null(typeof(VmTemplateCluster).GetProperty("Pending"));
        Assert.Equal(typeof(VmTemplateOperation), typeof(VmTemplateCluster).GetProperty("Operation")?.PropertyType);
    }

    private static bool DerivesFromProviderResource(Type type)
    {
        Type? current = type.BaseType;
        while (current != null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ProviderResource<>))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    [Fact] // the base carries state, never the role: a type on the provider still says whether it is a root or an entity
    public void EveryProviderResource_IsARootOrAnEntity()
    {
        List<string> roleless = typeof(ProviderResource<>).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.BaseType is { IsGenericType: true } b
                        && b.GetGenericTypeDefinition() == typeof(ProviderResource<>))
            .Where(t => typeof(IAggregateRoot).IsAssignableFrom(t) == typeof(IEntity).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(roleless.Count == 0, "Neither or both of IAggregateRoot and IEntity: " + string.Join(", ", roleless));

        // Vm, Network, FirewallRule, VmSnapshot, Volume, VolumeSnapshot and VmTemplateCluster are on the provider without the base, so the scan above does not reach them.
        Assert.True(typeof(IAggregateRoot).IsAssignableFrom(typeof(Vm)));
        Assert.False(typeof(IEntity).IsAssignableFrom(typeof(Vm)));
        Assert.True(typeof(IAggregateRoot).IsAssignableFrom(typeof(Network)));
        Assert.False(typeof(IEntity).IsAssignableFrom(typeof(Network)));
        Assert.True(typeof(IEntity).IsAssignableFrom(typeof(FirewallRule)));
        Assert.False(typeof(IAggregateRoot).IsAssignableFrom(typeof(FirewallRule)));
        Assert.True(typeof(IEntity).IsAssignableFrom(typeof(VmSnapshot)));
        Assert.False(typeof(IAggregateRoot).IsAssignableFrom(typeof(VmSnapshot)));
        Assert.True(typeof(IAggregateRoot).IsAssignableFrom(typeof(Volume)));
        Assert.False(typeof(IEntity).IsAssignableFrom(typeof(Volume)));
        Assert.True(typeof(IEntity).IsAssignableFrom(typeof(VolumeSnapshot)));
        Assert.False(typeof(IAggregateRoot).IsAssignableFrom(typeof(VolumeSnapshot)));
        Assert.True(typeof(IEntity).IsAssignableFrom(typeof(VmTemplateCluster)));
        Assert.False(typeof(IAggregateRoot).IsAssignableFrom(typeof(VmTemplateCluster)));
    }
}
