using System.Reflection;
using System.Text.RegularExpressions;
using Titan.Domain.Model;

namespace Titan.Tests.Domain;

/// The Domain is plain old code (titan-ops architecture/domain-methods.md#plain-code): no member of Titan.Domain, at any
/// access level, takes or returns a type of System.Linq.Expressions, IQueryable or Entity Framework, and the assembly
/// references no Entity Framework or Npgsql assembly. A rule that must also run in SQL is a plain bool on the aggregate;
/// the repository writes the SQL twin itself, and never reaches a Domain property by its string name (EF.Property),
/// which would go around `private set`.
///
/// Silent about: a lambda inside a method body (only signatures are read); Titan.Infrastructure/Migrations/, which the
/// EF.Property scan skips; and a repository query whose condition drifts from the bool — the *RepositoryStateTests run
/// every shape through both, and QueryRuleDbTests pins each side on Postgres.
public class DomainPlainCodeTests
{
    private static readonly Assembly DomainAssembly = typeof(AggregateRoot).Assembly;

    private static readonly string[] ForbiddenNamespaces =
        ["System.Linq.Expressions", "Microsoft.EntityFrameworkCore"];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }
        }
        if (type.HasElementType)
        {
            foreach (Type inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }
        }
    }

    private static bool IsForbidden(Type type)
    {
        foreach (Type part in Unwrap(type))
        {
            if (part == typeof(IQueryable) || (part.IsGenericType && part.GetGenericTypeDefinition() == typeof(IQueryable<>)))
            {
                return true;
            }
            string ns = part.Namespace ?? "";
            foreach (string forbidden in ForbiddenNamespaces)
            {
                if (ns == forbidden || ns.StartsWith(forbidden + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<(string Member, Type Type)> SignatureTypes(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(Declared))
        {
            yield return ($"{type.Name}.{property.Name}", property.PropertyType);
        }
        foreach (FieldInfo field in type.GetFields(Declared))
        {
            yield return ($"{type.Name}.{field.Name}", field.FieldType);
        }
        foreach (MethodInfo method in type.GetMethods(Declared))
        {
            if (method.IsSpecialName)
            {
                continue;
            }
            yield return ($"{type.Name}.{method.Name}", method.ReturnType);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return ($"{type.Name}.{method.Name}({parameter.Name})", parameter.ParameterType);
            }
        }
    }

    [Fact]
    public void NoDomainMember_TakesOrReturns_AnExpressionQueryableOrEntityFrameworkType()
    {
        var offenders = new List<string>();
        foreach (Type type in DomainAssembly.GetTypes().Where(t => !t.Name.StartsWith('<')))
        {
            foreach ((string member, Type memberType) in SignatureTypes(type))
            {
                if (IsForbidden(memberType))
                {
                    offenders.Add($"{member}: {memberType}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Not plain old code: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheDomainAssembly_ReferencesNoEntityFramework()
    {
        var referenced = DomainAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || n.StartsWith("Npgsql", StringComparison.Ordinal))
            .ToList();

        Assert.True(referenced.Count == 0, "Titan.Domain references: " + string.Join(", ", referenced));
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    private static readonly Regex PropertyByName = new(@"\bEF\.Property\b");

    // Scans the SOURCE: a string name does not break the build when the property is renamed, only the query at run time.
    [Fact]
    public void Infrastructure_ReachesNoDomainPropertyByItsName()
    {
        string root = SolutionRoot();
        var byName = new List<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "Titan.Infrastructure"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Titan.Infrastructure/Migrations/", StringComparison.Ordinal) || relative.Contains("/bin/") || relative.Contains("/obj/"))
            {
                continue;
            }
            if (PropertyByName.IsMatch(File.ReadAllText(path)))
            {
                byName.Add(relative);
            }
        }

        Assert.True(byName.Count == 0, "Reaches a Domain property by its string name (EF.Property): " + string.Join(", ", byName));
    }
}
