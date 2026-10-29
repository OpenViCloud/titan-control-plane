using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Api;

/// <summary>
/// Every catalog route the public contract promises is registered. Service-level tests never touch
/// routing, so a route that was never implemented stays green there while the VM create path needs it.
/// </summary>
// FullHost — runs in its own CI process: a WebApplicationFactory host sets FastEndpoints' global static
// ServiceResolver and disposes it at teardown, so sharing a pool with Factory.Create endpoint tests
// reopens a dispose race (ObjectDisposedException on CI).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class CatalogContractTests : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private readonly OpenApiSpecTests.ApiFactory _factory;
    private readonly HttpClient _client;

    public CatalogContractTests(OpenApiSpecTests.ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Adding a route to the public contract without adding it here (or vice versa) is the drift this catches.
    public static TheoryData<string> CatalogRoutes() => new()
    {
        "/v1/clusters",
        "/v1/templates",
        "/v1/vm-offerings",
        "/v1/volume-offerings",
    };

    // Any status but 404 proves the route is registered: clusters and the two price lists are anonymous, templates answer 401.
    [Theory]
    [MemberData(nameof(CatalogRoutes))]
    public async Task CatalogRoute_IsRegistered(string route)
    {
        var response = await _client.GetAsync(route);

        Assert.False(response.StatusCode == HttpStatusCode.NotFound,
            $"GET {route} -> 404. The route is not registered, but the public contract advertises it.");
    }

    // Only the price lists open up; a silent flip of templates to anonymous would expose the image catalog.
    [Theory]
    [InlineData("/v1/templates")]
    public async Task CatalogRoute_RequiresAuth(string route)
    {
        var response = await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The public site shows prices before sign-up (titan-ops features/public-site/design.md#live-pricing).
    // Rows come from the HasData seed, so an empty list can't pass as "anonymous and priced".
    [Theory]
    [InlineData("/v1/vm-offerings", "pricePerHour")]
    [InlineData("/v1/volume-offerings", "pricePerGbPerHour")]
    public async Task CatalogRoute_IsAnonymous(string route, string priceField)
    {
        SeedCatalog();

        var response = await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        List<JsonElement> rows = body.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        foreach (JsonElement row in rows)
        {
            decimal price = row.GetProperty(priceField).GetDecimal();
            Assert.True(price > 0, $"{route}: {priceField} = {price}");
        }
    }

    // A Terraform provider or CLI generates against the published spec, so a route missing from it is
    // unreachable for them even if it works over HTTP.
    [Theory]
    [MemberData(nameof(CatalogRoutes))]
    public async Task CatalogRoute_AppearsInPublishedSpec(string route)
    {
        var spec = await (await _client.GetAsync("/swagger/v1/swagger.json")).Content.ReadAsStringAsync();

        Assert.Contains($"\"{route}\"", spec);
    }

    private void SeedCatalog()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        db.Database.EnsureCreated();
    }
}
