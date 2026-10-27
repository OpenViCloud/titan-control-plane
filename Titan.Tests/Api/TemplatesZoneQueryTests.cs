using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.Platform.ApiToken;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Api;

/// `GET /v1/templates?zoneId=` is declared in the spec, so a client generated from it (CLI, Terraform provider) has
/// the parameter, and the host still reads it (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-zone).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class TemplatesZoneQueryTests(RealHostFactory factory) : IClassFixture<RealHostFactory>
{
    private async Task<HttpClient> ClientOfANewTenantAsync()
    {
        string kc = "kc-" + Guid.NewGuid();
        Tenant tenant = Tenant.CreatePending("t-" + Guid.NewGuid().ToString("N")[..8], kc, kc + "@titan.test");
        tenant.Approve();
        string secret = "ovc_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
            db.Tenants.Add(tenant);
            db.ApiTokens.Add(ApiToken.Create(tenant.Id, kc, 0, "templates", secret, null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        return client;
    }

    [Fact]
    public async Task TheSpec_DeclaresAnOptionalZoneIdQuery()
    {
        string raw = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        JsonElement operation = JsonDocument.Parse(raw).RootElement
            .GetProperty("paths").GetProperty("/v1/templates").GetProperty("get");

        JsonElement zoneId = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "zoneId");
        Assert.Equal("query", zoneId.GetProperty("in").GetString());
        Assert.False(zoneId.TryGetProperty("required", out JsonElement required) && required.GetBoolean());
    }

    [Fact]
    public async Task AnUnknownZone_IsZoneNotFound_AndNoZone_IsOk()
    {
        HttpClient client = await ClientOfANewTenantAsync();

        HttpResponseMessage unknown = await client.GetAsync("/v1/templates?zoneId=no-such-zone");
        HttpResponseMessage any = await client.GetAsync("/v1/templates");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("CLUSTER_NOT_FOUND", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, any.StatusCode);
    }
}
