using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.Platform.ApiToken;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Api;

/// A request with an API token goes through the host as Program.cs wires it: the token scheme, then
/// TenantResolutionMiddleware, then the endpoint (titan-ops features/public-api/design.md#api-token). The tokens are put
/// in the database directly; the secret is the bearer.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ApiTokenAuthTests(RealHostFactory factory) : IClassFixture<RealHostFactory>
{
    private static string FreshSecret() =>
        "ovc_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<Tenant> SeedTenantAsync(Action<Tenant>? change = null, bool approve = true)
    {
        string kc = "kc-" + Guid.NewGuid();
        Tenant tenant = Tenant.CreatePending("t-" + Guid.NewGuid().ToString("N")[..8], kc, kc + "@titan.test");
        if (approve)
        {
            tenant.Approve();
        }

        change?.Invoke(tenant);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<string> SeedTokenAsync(Tenant tenant, int? days = 90, DateTimeOffset? createdAt = null)
    {
        string secret = FreshSecret();
        string owner = tenant.Members.Single().KeycloakUserId;
        ApiToken token = ApiToken.Create(tenant.Id, owner, 0, "t-" + Guid.NewGuid().ToString("N")[..8], secret, days,
            createdAt ?? DateTimeOffset.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();
        return secret;
    }

    private HttpClient ClientWith(string? authorization)
    {
        HttpClient client = factory.CreateClient();
        if (authorization != null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        }

        return client;
    }

    private static HttpRequestMessage CreateTokenRequest(string secretForTheNewToken, string name = "ci")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/tenant/api-tokens")
        {
            Content = JsonContent.Create(new { name, token = secretForTheNewToken, expiresInDays = 30 }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    [Fact]
    public async Task AValidToken_ReachesTheTenantsRoutes_AsItsCreator()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant);

        HttpResponseMessage response = await ClientWith("Bearer " + secret).GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement listed = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
        Assert.Equal(secret[..ApiToken.PrefixLength], listed.GetProperty("prefix").GetString());
    }

    [Fact]
    public async Task TheSchemeWord_IsCaseInsensitive_AsForJwt()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant);

        HttpResponseMessage response = await ClientWith("bearer " + secret).GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ACreateMadeWithAToken_IsRecordedAsTheTokensCreator_AndStoresNoSecret()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant);
        string newSecret = FreshSecret();

        HttpResponseMessage response = await ClientWith("Bearer " + secret).SendAsync(CreateTokenRequest(newSecret));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        var events = await db.ActivityEvents.Where(e => e.TenantId == tenant.Id).ToListAsync();
        Assert.Contains(events, e => e.Action == "api_token.create_requested"
                                     && e.ActorKeycloakUserId == tenant.Members.Single().KeycloakUserId);
        var records = await db.IdempotencyRecords.Where(r => r.TenantId == tenant.Id).ToListAsync();
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.DoesNotContain(newSecret, (r.ResponseBody ?? "") + r.RequestHash));
        Assert.All(events, e => Assert.DoesNotContain(newSecret, e.ResourceName));
        Assert.All(await db.ApiTokens.Where(t => t.TenantId == tenant.Id).ToListAsync(),
            t => Assert.NotEqual(newSecret, t.SecretHash));
    }

    [Theory]
    [InlineData("Bearer ovc_")]
    [InlineData("Bearer ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2p wA")]
    [InlineData("Bearer ovc_q3v9Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQwA")] // well formed, never created
    public async Task AMalformedOrUnknownToken_Is401(string authorization)
    {
        HttpResponseMessage response = await ClientWith(authorization).GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoAuthorization_Is401()
    {
        HttpResponseMessage response = await ClientWith(null).GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredToken_Is401()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant, days: 30, createdAt: DateTimeOffset.UtcNow.AddDays(-31));

        HttpResponseMessage response = await ClientWith("Bearer " + secret).GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ADeletedToken_Is401_OnTheVeryNextRequest()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant);
        HttpClient client = ClientWith("Bearer " + secret);
        JsonElement listed = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/v1/tenant/api-tokens"))!);

        HttpResponseMessage deleted = await client.DeleteAsync("/v1/tenant/api-tokens/" + listed.GetProperty("id").GetGuid());
        HttpResponseMessage after = await client.GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ATokenWhoseMemberIsGone_Is401_Not500()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
            db.Members.RemoveRange(db.Members.Where(m => m.TenantId == tenant.Id));
            await db.SaveChangesAsync();
        }

        HttpResponseMessage response = await ClientWith("Bearer " + secret).GetAsync("/v1/tenant/api-tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASuspendedTenant_ReadsWithAToken_ButCannotWrite()
    {
        Tenant tenant = await SeedTenantAsync(t => t.Suspend());
        string secret = await SeedTokenAsync(tenant);
        HttpClient client = ClientWith("Bearer " + secret);

        HttpResponseMessage read = await client.GetAsync("/v1/tenant/api-tokens");
        HttpResponseMessage write = await client.SendAsync(CreateTokenRequest(FreshSecret()));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("TENANT_SUSPENDED", (await write.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task APendingTenant_IsNotApprovedByAToken_AndCannotWrite()
    {
        Tenant tenant = await SeedTenantAsync(approve: false);
        string secret = await SeedTokenAsync(tenant);
        HttpClient client = ClientWith("Bearer " + secret);

        HttpResponseMessage write = await client.SendAsync(CreateTokenRequest(FreshSecret()));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("TENANT_PENDING_APPROVAL",
            (await write.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        Assert.Equal(TenantStatus.PendingApproval, (await db.Tenants.SingleAsync(t => t.Id == tenant.Id)).Status);
    }

    [Fact]
    public async Task AToken_DoesNotOpenAdminRoutes()
    {
        Tenant tenant = await SeedTenantAsync();
        string secret = await SeedTokenAsync(tenant);

        HttpResponseMessage response = await ClientWith("Bearer " + secret).GetAsync("/v1/tenants");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ATokenOfTenantA_CannotDeleteATokenOfTenantB()
    {
        Tenant a = await SeedTenantAsync();
        Tenant b = await SeedTenantAsync();
        string secretA = await SeedTokenAsync(a);
        string secretB = await SeedTokenAsync(b);
        JsonElement tokenOfB = Assert.Single(
            (await ClientWith("Bearer " + secretB).GetFromJsonAsync<JsonElement[]>("/v1/tenant/api-tokens"))!);

        HttpResponseMessage response = await ClientWith("Bearer " + secretA)
            .DeleteAsync("/v1/tenant/api-tokens/" + tokenOfB.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientWith("Bearer " + secretB).GetAsync("/v1/tenant/api-tokens")).StatusCode);
    }
}
