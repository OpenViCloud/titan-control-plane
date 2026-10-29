using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Platform.ApiToken;

/// A long-lived bearer a tenant member creates for Terraform, CI and the CLI
/// (titan-ops features/public-api/design.md#api-token). It acts as the member who created it. Only a hash of the
/// secret is kept: the portal makes the secret and shows it once. Titan-only, deleted outright when revoked.
public partial class ApiToken
{
    public const int MaxNameLength = 64;
    public const int MaxPerTenant = 20;
    public const int PrefixLength = 8;
    public static readonly IReadOnlyList<int> Lifetimes = [30, 90, 365];

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// The member who created it; requests made with the token act as this user.
    public string KeycloakUserId { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    /// The first characters of the secret, enough for the customer to tell tokens apart.
    public string Prefix { get; private set; } = default!;

    /// SHA-256 of the secret, lowercase hex.
    public string SecretHash { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    /// Null: never expires.
    public DateTimeOffset? ExpiresAt { get; private set; }

    private ApiToken() { }

    /// tokenCount is how many tokens the tenant already has, expired ones included.
    public static ApiToken Create(
        Guid tenantId, string keycloakUserId, int tokenCount, string name, string secret, int? expiresInDays,
        DateTimeOffset now)
    {
        if (tokenCount >= MaxPerTenant)
        {
            throw new BusinessException("API_TOKEN_QUOTA_EXCEEDED", $"API token limit reached (max {MaxPerTenant}).");
        }

        string trimmedName = name.Trim();
        if (trimmedName.Length == 0 || trimmedName.Length > MaxNameLength)
        {
            throw new BadRequestException("INVALID_API_TOKEN_NAME", $"The token name must be 1 to {MaxNameLength} characters.");
        }

        if (!IsWellFormed(secret))
        {
            throw new BadRequestException("INVALID_API_TOKEN", "The token must be ovc_ followed by 43 base64url characters.");
        }

        bool lifetimeAllowed = expiresInDays == null || Lifetimes.Contains(expiresInDays.Value);
        if (!lifetimeAllowed)
        {
            throw new BadRequestException("INVALID_API_TOKEN", "The token lifetime must be 30, 90 or 365 days, or none.");
        }

        DateTimeOffset? expiresAt = null;
        if (expiresInDays != null)
        {
            expiresAt = now.AddDays(expiresInDays.Value);
        }

        return new ApiToken
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeycloakUserId = keycloakUserId,
            Name = trimmedName,
            Prefix = secret[..PrefixLength],
            SecretHash = HashOf(secret),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
    }

    public static bool IsWellFormed(string secret) => SecretShape().IsMatch(secret);

    public static string HashOf(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// Expired at its expiry instant, not one tick later.
    public bool IsUsableAt(DateTimeOffset now)
    {
        if (ExpiresAt == null)
        {
            return true;
        }

        return now < ExpiresAt.Value;
    }

    [GeneratedRegex("^ovc_[A-Za-z0-9_-]{43}$")]
    private static partial Regex SecretShape();
}
