using System.Reflection;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// The pending operation and the last failure live once in ProviderResource (titan-ops
// architecture/domain-methods.md#provider-resource), for roots and entities alike. Every Domain type that has a Pending
// operation goes through it, so the state is never written again in a type of its own. Vm is the one named exception:
// its one status says what its waiting request asks (titan-ops features/vm/design.md#vm-status), so it keeps the request's
// id and the last failure itself and has no Pending.
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
        Assert.True(withPending.Count >= 7, $"the scan found only {withPending.Count} types with a pending operation");
        // Vm has no Pending, so this scan does not reach it: Vm_KeepsItsOperationInASingleVmOperation_AndNoTypeCopiesTheBasesState holds it.
        Assert.DoesNotContain(typeof(Vm), withPending);

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

        // Vm is on the provider without the base, so the scan above does not reach it.
        Assert.True(typeof(IAggregateRoot).IsAssignableFrom(typeof(Vm)));
        Assert.False(typeof(IEntity).IsAssignableFrom(typeof(Vm)));
    }
}
