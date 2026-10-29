using System.Security.Cryptography;
using System.Text;

namespace Titan.Infrastructure.Endpoints.Payments;

// Constant-time, to avoid a timing side-channel on the secret. Separate from X-Internal-Token: SePay is an
// Internet-facing caller with its own key (titan-ops architecture/money-and-wallet.md#sepay-writer).
internal static class SePayApiKeyAuth
{
    private const string Scheme = "Apikey ";

    /// True when the Authorization header carries "Apikey <key>" whose key byte-equals the expected one.
    public static bool Matches(string? authorizationHeader, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(authorizationHeader))
            return false;
        if (!authorizationHeader.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var provided = authorizationHeader[Scheme.Length..].Trim();
        if (provided.Length == 0) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }
}
