using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Values;

/// A network's name as the customer typed it. The shape rule lives here so no path — API, handler or test seam —
/// builds a network with a name the /v1 contract never allowed (titan-ops architecture/code-structure.md#logic-placement); the
/// validator reads Pattern and MaxLength and answers 400 in its own words. Also runs when EF materialises a row, so a
/// stored name that breaks the rule fails loudly at load instead of being served.
public sealed record NetworkName : ValueObject
{
    public const int MaxLength = 100;

    /// The validator's Matches() copies this string into api/openapi-v1.json as the field's `pattern`, so changing it
    /// changes the /v1 spec.
    public const string Pattern = "^[a-zA-Z0-9][a-zA-Z0-9_-]*$";

    private static readonly Regex Shape = new(Pattern, RegexOptions.Compiled);

    public string Value { get; private init; } = default!;

    private NetworkName() { }

    public static NetworkName Parse(string? value)
    {
        if (!TryParse(value, out NetworkName? name))
        {
            throw new BusinessException("INVALID_NETWORK_NAME",
                $"A network name is 1 to {MaxLength} letters, digits, hyphens or underscores, starting with a letter or digit.");
        }
        return name;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out NetworkName? name)
    {
        name = null;
        if (value == null || value.Length == 0 || value.Length > MaxLength || !Shape.IsMatch(value))
        {
            return false;
        }
        name = new NetworkName { Value = value };
        return true;
    }

    public override string ToString() => Value;
}
