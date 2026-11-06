using System.Reflection;
using Titan.Domain.Model;

namespace Titan.Tests.Domain;

/// The verb at the front of a Domain method says what it hands back (titan-ops architecture/domain-methods.md#method-verbs):
/// Get* returns the thing or throws, Find* returns it or null, Ensure* checks the aggregate's own state and returns
/// nothing. An input is checked where it arrives: a predicate (IsAName) and a throw at the call site, so no method is
/// named Require*: the word promised that something must be present, and what these checked was a shape or a ban.
///
/// Silent about: whether a Get* throws when the thing is missing (only the return type is read), whether an Ensure*
/// reads the state rather than an input, the repository interfaces (Titan.Domain.Repositories, not yet named this
/// way), and code outside Titan.Domain.
public class DomainMethodNameTests
{
    private static readonly Assembly DomainAssembly = typeof(IAggregateRoot).Assembly;

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly NullabilityInfoContext Nullability = new();

    private static List<MethodInfo> DomainMethodsStartingWith(string verb)
    {
        List<MethodInfo> methods = new();
        foreach (Type type in DomainAssembly.GetTypes())
        {
            bool inTheDomain = type.Namespace != null && type.Namespace.StartsWith("Titan.Domain", StringComparison.Ordinal);
            bool aRepository = type.Namespace == "Titan.Domain.Repositories";
            if (!inTheDomain || aRepository)
            {
                continue;
            }
            foreach (MethodInfo method in type.GetMethods(Declared))
            {
                bool startsWithTheVerb = method.Name.StartsWith(verb, StringComparison.Ordinal)
                                         && method.Name.Length > verb.Length
                                         && char.IsUpper(method.Name[verb.Length]);
                if (startsWithTheVerb && !method.IsSpecialName)
                {
                    methods.Add(method);
                }
            }
        }
        return methods;
    }

    private static string NameOf(MethodInfo method) => $"{method.DeclaringType!.Name}.{method.Name}";

    /// The type the caller ends up holding: the result of a Task, or the return type itself.
    private static NullabilityState ResultNullability(MethodInfo method)
    {
        NullabilityInfo info = Nullability.Create(method.ReturnParameter);
        bool isTask = method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>);
        if (isTask)
        {
            info = info.GenericTypeArguments[0];
        }
        if (Nullable.GetUnderlyingType(info.Type) != null)
        {
            return NullabilityState.Nullable;
        }
        return info.ReadState;
    }

    [Fact]
    public void NoMethod_IsNamedRequire()
    {
        List<string> offenders = DomainMethodsStartingWith("Require")
            .Select(NameOf)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "An input is checked where it arrives, with a predicate and a throw; a state check is Ensure*:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryEnsure_ReturnsNothing()
    {
        const string verb = "Ensure";
        List<MethodInfo> methods = DomainMethodsStartingWith(verb);
        Assert.NotEmpty(methods); // 0 offenders out of 0 methods reads identical to a clean scan

        List<string> offenders = methods
            .Where(m => m.ReturnType != typeof(void) && m.ReturnType != typeof(Task))
            .Select(m => $"{NameOf(m)} returns {m.ReturnType.Name}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"A {verb}* method only checks; one that hands something back is a Get* or a Find*:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryFind_MayAnswerNull()
    {
        List<MethodInfo> methods = DomainMethodsStartingWith("Find");
        Assert.NotEmpty(methods);

        List<string> offenders = methods
            .Where(m => m.ReturnType == typeof(void) || ResultNullability(m) != NullabilityState.Nullable)
            .Select(NameOf)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A Find* answers null when the thing is missing; one that never does is a Get*:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryGet_HandsBackTheThing()
    {
        List<MethodInfo> methods = DomainMethodsStartingWith("Get");
        Assert.NotEmpty(methods);

        List<string> offenders = methods
            .Where(m => m.ReturnType == typeof(void) || ResultNullability(m) == NullabilityState.Nullable)
            .Select(NameOf)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A Get* returns the thing or throws; one that may answer null is a Find*:\n  "
            + string.Join("\n  ", offenders));
    }
}
