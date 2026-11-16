using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace Titan.Tests.Api;

// The console route on the real pipeline (titan-ops features/vm/design.md#vm-console): a browser sends no token, so the
// route lets an upgrade without one through authentication, and the ticket in its query decides; the websocket stays
// out of the OpenAPI spec while the ticket route is in it.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ConsoleSessionHostTests(OpenApiSpecTests.ApiFactory factory) : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private static readonly string Route = $"/v1/tenant/vms/{Guid.NewGuid()}/console";

    [Fact]
    public async Task AWebSocketUpgradeWithAnUnknownTicket_Is401()
    {
        factory.CreateClient();
        WebSocketClient client = factory.Server.CreateWebSocketClient();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConnectAsync(new Uri(factory.Server.BaseAddress, Route + "?ticket=ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                CancellationToken.None));

        Assert.Contains("401", refused.Message);
    }

    [Fact]
    public async Task APlainGet_Is400_WebSocketRequired()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync(Route);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WEBSOCKET_REQUIRED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheTicketRoute_NeedsAToken()
    {
        HttpResponseMessage response = await factory.CreateClient().PostAsync(
            $"/v1/tenant/vms/{Guid.NewGuid()}/console-tickets", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheWebSocket_IsNotInTheOpenApiSpec_AndTheTicketRouteIs()
    {
        string spec = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        using JsonDocument document = JsonDocument.Parse(spec);
        string[] paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(paths, p => p.EndsWith("/console"));
        Assert.Contains(paths, p => p.EndsWith("/console-tickets"));
    }
}
