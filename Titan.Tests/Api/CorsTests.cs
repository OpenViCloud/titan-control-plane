using System.Net;

namespace Titan.Tests.Api;

file sealed class CorsTestFactory(params string[] allowedOrigins) : RealHostFactory(
    new Dictionary<string, string?>
    {
        ["Internal:Token"] = "test-token",
        ["Keycloak:Authority"] = "http://keycloak.test/realms/titan",
    },
    allowedOrigins.Select((origin, i) => (Key: $"Cors:AllowedOrigins:{i}", Value: origin)).ToDictionary(e => e.Key, e => e.Value))
{
}

// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class CorsTests
{
    [Fact]
    public async Task AllowedOrigin_PrefligthRequest_ReturnsCorsHeader()
    {
        const string allowedOrigin = "https://portal.example.com";
        await using var factory = new CorsTestFactory(allowedOrigin);
        await factory.InitializeAsync();

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/v1/clusters");
        request.Headers.Add("Origin", allowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "Expected Access-Control-Allow-Origin header");
        Assert.Equal(allowedOrigin,
            response.Headers.GetValues("Access-Control-Allow-Origin").First());
    }

    [Fact]
    public async Task UnknownOrigin_PreflightRequest_NoCorsHeader()
    {
        await using var factory = new CorsTestFactory("https://portal.example.com");
        await factory.InitializeAsync();

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/v1/clusters");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            "Expected no Access-Control-Allow-Origin header for unknown origin");
    }

    [Fact]
    public async Task EmptyAllowedOrigins_AppStartsAndNoCorsHeader()
    {
        await using var factory = new CorsTestFactory(); // no origins
        await factory.InitializeAsync();

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/v1/clusters");
        request.Headers.Add("Origin", "https://portal.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
