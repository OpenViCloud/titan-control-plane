using Microsoft.AspNetCore.Http;

namespace Titan.Infrastructure.Auth;

/// Two ways to authenticate, one Authorization header: a bearer that starts with ovc_ is an API token
/// (titan-ops features/public-api/design.md#api-token), anything else goes to Keycloak's JWT as before. The scheme word is
/// matched without regard to case, as JwtBearer does.
public static class ApiTokenSchemes
{
    public const string Selector = "BearerOrApiToken";
    public const string ApiToken = "ApiToken";

    /// JwtBearerDefaults.AuthenticationScheme; that package is referenced by Titan.Host only.
    public const string Jwt = "Bearer";
    private const string BearerPrefix = "Bearer ";
    private const string TokenPrefix = "ovc_";

    /// The token after "Bearer ", or null when the header does not carry an API token.
    public static string? ReadToken(string? authorization)
    {
        if (authorization == null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string value = authorization[BearerPrefix.Length..].Trim();
        if (!value.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return value;
    }

    public static string Choose(HttpContext context)
    {
        string? token = ReadToken(context.Request.Headers.Authorization);
        if (token != null)
        {
            return ApiToken;
        }

        return Jwt;
    }
}
