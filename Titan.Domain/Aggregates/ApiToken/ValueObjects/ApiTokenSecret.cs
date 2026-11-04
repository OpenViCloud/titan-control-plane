using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.ApiToken;

/// The rules of the secret itself: what one looks like and what is kept of it. A token is created with them and a
/// call is authenticated with them, so neither side owns them. Titan never stores a secret, so no instance exists.
public sealed partial record ApiTokenSecret : ValueObject
{
    private ApiTokenSecret() { }

    public static bool IsWellFormed(string secret) => SecretShape().IsMatch(secret);

    public static string HashOf(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    [GeneratedRegex("^ovc_[A-Za-z0-9_-]{43}$")]
    private static partial Regex SecretShape();
}
