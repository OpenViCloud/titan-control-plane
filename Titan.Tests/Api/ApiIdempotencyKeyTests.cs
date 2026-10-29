using FastEndpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Titan.Infrastructure.Auth;

namespace Titan.Tests.Api;

// titan-ops architecture/http-contract.md#idempotency-key. The middleware decides from the ROUTED endpoint's
// FastEndpoints definition, so this reads the running host's routing table: a marker it does not expose is
// enforced nowhere. Fails both ways (unmarked create, marked non-create); silent about a new create that is
// neither listed here nor marked.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ApiIdempotencyKeyTests(OpenApiSpecTests.ApiFactory factory)
    : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    // Every create of titan-ops architecture/http-contract.md#create-returns-id.
    private static readonly string[] Creates =
    [
        "POST v1/tenant/vms",
        "POST v1/tenant/networks",
        "POST v1/tenant/networks/{networkId:guid}/firewall-rules",
        "POST v1/tenant/volumes",
        "POST v1/tenant/vms/{vmId:guid}/snapshots",
        "POST v1/tenant/volumes/{volumeId:guid}/snapshots",
        "POST v1/tenant/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}/restore",
        "POST v1/tenant/topups",
        "POST v1/tenant/ssh-keys",
        "POST v1/tenant/api-tokens",
        "POST v1/tenant/load-balancers",
        "POST v1/admin/topups/grant",
    ];

    [Fact]
    public void EveryCreate_RequiresAnIdempotencyKey()
    {
        factory.CreateClient(); // starts the host so the routing table exists
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        var routed = source.Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Def: e.Metadata.GetMetadata<EndpointDefinition>(), Route: e.RoutePattern.RawText ?? "",
                          Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(x => x.Def is not null)
            .ToList();
        Assert.True(routed.Count > 30, $"only {routed.Count} FastEndpoints routes seen — the scan is blind");

        var marked = routed
            .Where(x => typeof(IRequiresIdempotencyKey).IsAssignableFrom(x.Def!.EndpointType))
            .SelectMany(x => x.Methods.Select(m => $"{m} {x.Route.TrimStart('/')}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Creates.OrderBy(x => x, StringComparer.Ordinal), marked);
    }
}
