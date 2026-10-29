using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace Titan.Tests.Api;

// The SSH session route on the real pipeline (titan-ops features/vm/design.md#ssh-session): the websocket upgrade goes
// through the same JWT authentication as every route, and the route stays out of the OpenAPI spec the CLI client is
// generated from.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class SshSessionHostTests(OpenApiSpecTests.ApiFactory factory) : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private static readonly string Route = $"/v1/tenant/vms/{Guid.NewGuid()}/ssh";

    [Fact]
    public async Task APlainGetWithoutAToken_Is401()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWebSocketUpgradeWithoutAToken_Is401()
    {
        factory.CreateClient();
        WebSocketClient client = factory.Server.CreateWebSocketClient();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConnectAsync(new Uri(factory.Server.BaseAddress, Route), CancellationToken.None));

        Assert.Contains("401", refused.Message);
    }

    // TenantResolutionMiddleware reads IsWebSocketRequest, which only UseWebSockets sets: registered after it, a suspended
    // tenant's upgrade would read as a plain GET and pass. The middleware tests cannot see the order; this reads it.
    [Fact]
    public void UseWebSockets_RunsBeforeTheTenantMiddleware()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Host", "Program.cs"));

        int webSockets = program.IndexOf("app.UseWebSockets(", StringComparison.Ordinal);
        int tenant = program.IndexOf("app.UseMiddleware<TenantResolutionMiddleware>()", StringComparison.Ordinal);
        Assert.True(webSockets >= 0, "Program.cs does not call app.UseWebSockets");
        Assert.True(tenant >= 0, "Program.cs does not register TenantResolutionMiddleware");
        Assert.True(webSockets < tenant, "UseWebSockets must come before TenantResolutionMiddleware");
    }

    [Fact]
    public async Task TheRoute_IsNotInTheOpenApiSpec()
    {
        string spec = await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        using JsonDocument document = JsonDocument.Parse(spec);

        bool listed = document.RootElement.GetProperty("paths").EnumerateObject()
            .Any(path => path.Name.EndsWith("/ssh"));
        Assert.False(listed);
    }
}
