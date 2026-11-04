using System.Reflection;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// The waiting request and the last failure live once in RequestingRoot and RequestingChild
// (titan-ops architecture/resource-lifecycle.md#shape). The two carry the same members, because a type has one base
// and the role is the base; and every Domain type that has a Pending request goes through one of them, so the state is
// never written again in a type of its own.
public class RequestingBaseTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string[] Members(Type type) =>
        type.GetMembers(Declared)
            .Where(m => !m.Name.StartsWith('<') && m is not ConstructorInfo)
            .Select(m => m.MemberType + " " + m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void RequestingRoot_AndRequestingChild_CarryTheSameMembers()
    {
        string[] root = Members(typeof(RequestingRoot<>));
        Assert.NotEmpty(root);
        Assert.Equal(root, Members(typeof(RequestingChild<>)));
    }

    [Fact]
    public void EveryTypeWithAPendingRequest_GetsItFromABase()
    {
        List<Type> withPending = typeof(RequestingRoot<>).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetProperty("Pending")?.PropertyType is { IsGenericType: true } p
                        && p.GetGenericTypeDefinition() == typeof(PendingRequest<>))
            .ToList();
        Assert.True(withPending.Count >= 8, $"the scan found only {withPending.Count} types with a pending request");

        HashSet<string> baseNames = typeof(RequestingRoot<>).GetMembers(Declared)
            .Where(m => !m.Name.StartsWith('<') && m is not ConstructorInfo)
            .Select(m => m.Name)
            .ToHashSet();
        List<string> declaringItThemselves = withPending
            .SelectMany(t => t.GetMembers(Declared).Where(m => baseNames.Contains(m.Name)).Select(m => t.Name + "." + m.Name))
            .ToList();
        Assert.True(declaringItThemselves.Count == 0, "Declares the base's state again: " + string.Join(", ", declaringItThemselves));
    }
}
