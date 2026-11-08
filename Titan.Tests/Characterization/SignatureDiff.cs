using System.Reflection;

namespace Titan.Tests.Characterization;

/// The API surface a change touches (titan-ops architecture/measurement.md#measurement): the public and internal
/// instance members of a type, inherited ones included, as text. Types are named by their simple name, so the frozen
/// copy in Old/ and the live type compare equal when only the namespace differs. Added is what the second type has and
/// the first does not; Removed is the reverse.
internal sealed record SignatureDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public static SignatureDiff Between(Type before, Type after)
    {
        IReadOnlyList<string> was = Surface(before);
        IReadOnlyList<string> now = Surface(after);
        return new SignatureDiff(now.Except(was).ToList(), was.Except(now).ToList());
    }

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;

    public override string ToString() =>
        $"added: [{string.Join("; ", Added)}]\nremoved: [{string.Join("; ", Removed)}]";

    public static IReadOnlyList<string> Surface(Type type)
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var signatures = new List<string>();
        foreach (PropertyInfo property in type.GetProperties(Instance))
        {
            MethodInfo accessor = (property.GetMethod ?? property.SetMethod)!;
            if (VisibilityOf(accessor) is { } visibility)
            {
                signatures.Add($"{visibility} {property.Name}: {Simple(property.PropertyType)}");
            }
        }
        foreach (FieldInfo field in type.GetFields(Instance))
        {
            if (VisibilityOf(field.IsPublic, field.IsAssembly, field.IsFamilyOrAssembly) is { } visibility)
            {
                signatures.Add($"{visibility} {field.Name}: {Simple(field.FieldType)}");
            }
        }
        foreach (MethodInfo method in type.GetMethods(Instance))
        {
            bool isAccessor = method.IsSpecialName;
            if (isAccessor || method.DeclaringType == typeof(object) || VisibilityOf(method) is not { } visibility)
            {
                continue;
            }
            string parameters = string.Join(", ", method.GetParameters().Select(p => Simple(p.ParameterType)));
            signatures.Add($"{visibility} {method.Name}({parameters}): {Simple(method.ReturnType)}");
        }
        return signatures.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static string? VisibilityOf(MethodBase method) =>
        VisibilityOf(method.IsPublic, method.IsAssembly, method.IsFamilyOrAssembly);

    private static string? VisibilityOf(bool isPublic, bool isInternal, bool isProtectedInternal)
    {
        if (isPublic)
        {
            return "public";
        }
        return isInternal || isProtectedInternal ? "internal" : null;
    }

    private static string Simple(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return Simple(underlying) + "?";
        }
        if (!type.IsGenericType)
        {
            return type.Name;
        }
        string name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Simple))}>";
    }
}
