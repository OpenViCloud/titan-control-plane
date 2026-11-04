using System.Reflection;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// The pending operation and the last failure live once in ProviderResource (titan-ops
// architecture/domain-methods.md#provider-resource), for roots and entities alike. Every Domain type that has a Pending
// operation goes through it, so the state is never written again in a type of its own.
public class ProviderResourceTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void EveryTypeWithAPendingOperation_GetsItFromProviderResource()
    {
        List<Type> withPending = typeof(ProviderResource<>).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetProperty("Pending")?.PropertyType is { IsGenericType: true } p
                        && p.GetGenericTypeDefinition() == typeof(PendingOperation<>))
            .ToList();
        Assert.True(withPending.Count >= 8, $"the scan found only {withPending.Count} types with a pending operation");

        HashSet<string> baseNames = typeof(ProviderResource<>).GetMembers(Declared)
            .Where(m => !m.Name.StartsWith('<') && m is not ConstructorInfo)
            .Select(m => m.Name)
            .ToHashSet();
        List<string> declaringItThemselves = withPending
            .SelectMany(t => t.GetMembers(Declared).Where(m => baseNames.Contains(m.Name)).Select(m => t.Name + "." + m.Name))
            .ToList();
        Assert.True(declaringItThemselves.Count == 0, "Declares the base's state again: " + string.Join(", ", declaringItThemselves));
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
    }
}
