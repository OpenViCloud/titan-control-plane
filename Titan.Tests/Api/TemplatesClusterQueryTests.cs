using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Api;

/// `GET /v1/templates?clusterId=` is declared in the spec, so a client generated from it (CLI, Terraform provider) has
/// the parameter, and the host still reads it (titan-ops features/catalog/design.md#template-per-cluster).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class TemplatesClusterQueryTests(RealHostFactory factory) : IClassFixture<RealHostFactory>
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
    public async Task TheSpec_DeclaresAnOptionalClusterIdQuery()
    {
        string raw = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        JsonElement operation = JsonDocument.Parse(raw).RootElement
            .GetProperty("paths").GetProperty("/v1/templates").GetProperty("get");

        JsonElement clusterId = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "clusterId");
        Assert.Equal("query", clusterId.GetProperty("in").GetString());
        Assert.False(clusterId.TryGetProperty("required", out JsonElement required) && required.GetBoolean());
    }

    [Fact]
    public async Task AnUnknownCluster_IsClusterNotFound_AndNoCluster_IsOk()
    {
        HttpClient client = await ClientOfANewTenantAsync();

        HttpResponseMessage unknown = await client.GetAsync("/v1/templates?clusterId=no-such-cluster");
        HttpResponseMessage any = await client.GetAsync("/v1/templates");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("CLUSTER_NOT_FOUND", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, any.StatusCode);
    }
}
