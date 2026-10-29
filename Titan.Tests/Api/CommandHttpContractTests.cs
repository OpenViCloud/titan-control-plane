using System.Text.RegularExpressions;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Titan.Tests.Api;

// titan-ops architecture/http-contract.md#commands-return-no-data: PUT/PATCH answer 204 with an empty body; the named
// async POST actions answer 202 with an empty body (titan-ops architecture/http-contract.md#async-actions-return-202).
// WHICH endpoints are PUT/PATCH comes from the running host's EndpointDataSource; the status-code half is textual
// (it reads the endpoint's own file). Silent about an endpoint that sets Response.StatusCode by hand or writes the
// body stream directly, and about a NEW async POST action: no scan here finds one.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class CommandHttpContractTests(OpenApiSpecTests.ApiFactory factory)
    : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    // Pinned so a PUT/PATCH that disappears from the scan (renamed verb, endpoint not registered) fails
    // loudly instead of shrinking the checked set. A new route raises it on purpose.
    private const int PutAndPatchRoutes = 22;

    private static readonly Regex SendsABody = new(@"\bSend(Ok|)Async\(", RegexOptions.Compiled);

    private static readonly string EndpointsDir =
        Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "Endpoints");

    private List<(EndpointDefinition Def, string Verb, string Route)> CommandRoutes(params string[] verbs)
    {
        factory.CreateClient(); // starts the host so the routing table exists
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        return source.Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var def = e.Metadata.GetMetadata<EndpointDefinition>();
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                return def is null
                    ? []
                    : methods.Where(verbs.Contains).Select(m => (def, m, e.RoutePattern.RawText ?? ""));
            })
            .ToList();
    }

    private static string SourceOf(Type endpoint)
    {
        var files = Directory.EnumerateFiles(EndpointsDir, $"{endpoint.Name}.cs", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count == 1,
            $"Expected exactly one source file named {endpoint.Name}.cs under {EndpointsDir}, found {files.Count}. "
            + "This guard maps an endpoint type to its file by name.");
        return File.ReadAllText(files[0]);
    }

    [Fact] // titan-ops architecture/http-contract.md#commands-return-no-data
    public void EveryPutAndPatch_DeclaresNoResponseType_AndSendsNoContent()
    {
        var routes = CommandRoutes(HttpMethods.Put, HttpMethods.Patch);

        // An empty scan would pass everything below while checking nothing.
        Assert.Equal(PutAndPatchRoutes, routes.Count);

        var offenders = new List<string>();
        foreach (var (def, verb, route) in routes)
        {
            if (def.ResDtoType != typeof(object))
                offenders.Add($"{verb} {route}: declares response type {def.ResDtoType.Name}");

            var src = SourceOf(def.EndpointType);
            if (!src.Contains("SendNoContentAsync(", StringComparison.Ordinal))
                offenders.Add($"{verb} {route}: never calls SendNoContentAsync");
            if (SendsABody.IsMatch(src))
                offenders.Add($"{verb} {route}: calls SendOkAsync/SendAsync");
        }

        Assert.True(offenders.Count == 0,
            "A PUT/PATCH answers with something other than 204 and an empty body. A command returns no "
            + "data (docs/architecture/http-contract.md#commands-return-no-data); a caller that needs the new state reads it back through a query. "
            + "Stop and ask before adding an exception (T-1): " + string.Join(" | ", offenders));
    }

    // titan-ops architecture/http-contract.md#async-actions-return-202 — named routes, because "async POST action" is not something a scan can recognise.
    public static TheoryData<string> AsyncPostActions => new()
    {
        "v1/tenant/billing/invoices/{invoiceId:guid}/pay",
        "v1/admin/invoices/{invoiceId:guid}/settle-from-wallet",
        "v1/admin/topups/{id:guid}/confirm",
        // Not money, but the same shape: the enqueue is async and the worker reconciles later.
        "v1/admin/templates/sync",
    };

    // Async requests that enqueue a provider command: `202 {}` still carries a body, and
    // titan-ops architecture/http-contract.md#async-actions-return-202 says a command returns none.
    public static TheoryData<string, string> AsyncProviderRequests => new()
    {
        { HttpMethods.Delete, "v1/tenant/vms/{vmId:guid}" },
        { HttpMethods.Post, "v1/tenant/vms/{vmId:guid}/stop" },
        { HttpMethods.Post, "v1/tenant/vms/{vmId:guid}/start" },
        { HttpMethods.Delete, "v1/tenant/networks/{networkId:guid}" },
        { HttpMethods.Delete, "v1/tenant/vms/{vmId:guid}/snapshots/{snapshotId:guid}" },
        { HttpMethods.Post, "v1/tenant/vms/{vmId:guid}/snapshots/{snapshotId:guid}/revert" },
        { HttpMethods.Delete, "v1/tenant/volumes/{volumeId:guid}" },
        { HttpMethods.Post, "v1/tenant/volumes/{volumeId:guid}/attach" },
        { HttpMethods.Post, "v1/tenant/volumes/{volumeId:guid}/detach" },
        { HttpMethods.Delete, "v1/tenant/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}" },
        { HttpMethods.Delete, "v1/tenant/load-balancers/{loadBalancerId:guid}" },
    };

    [Theory]
    [MemberData(nameof(AsyncProviderRequests))]
    public void AsyncProviderRequest_SendsAcceptedWithNoBody(string verb, string route)
    {
        var match = CommandRoutes(verb).Where(r => r.Route == route).ToList();
        var (def, _, _) = Assert.Single(match);

        Assert.True(def.ResDtoType == typeof(object),
            $"{verb} {route} declares response type {def.ResDtoType.Name}; docs/architecture/http-contract.md#async-actions-return-202 says it returns no data.");

        var src = SourceOf(def.EndpointType);
        Assert.Contains("Status202Accepted", src);
        Assert.False(SendsABody.IsMatch(src), $"{verb} {route} sends a body — `202 {{}}` is still a body (docs/architecture/http-contract.md#async-actions-return-202).");
    }

    [Fact]
    public void NoEndpoint_AnswersAcceptedWithAnEmptyObject()
    {
        var files = Directory.EnumerateFiles(EndpointsDir, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 50, $"Only {files.Count} endpoint files found — the scan is blind.");

        var offenders = files
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"SendAsync\(\s*new\s*\{\s*\}\s*,\s*202"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "These endpoints answer `202 {}`: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(AsyncPostActions))]
    public void AsyncPostAction_DeclaresNoResponseType_AndSendsAcceptedWithNoBody(string route)
    {
        var match = CommandRoutes(HttpMethods.Post).Where(r => r.Route == route).ToList();
        var (def, _, _) = Assert.Single(match);

        Assert.True(def.ResDtoType == typeof(object),
            $"POST {route} declares response type {def.ResDtoType.Name}; docs/architecture/http-contract.md#async-actions-return-202 says it returns no data.");

        var src = SourceOf(def.EndpointType);
        Assert.Contains("Status202Accepted", src);
        Assert.False(SendsABody.IsMatch(src),
            $"POST {route} sends a body. The money is still in the outbox when this response leaves "
            + "(docs/architecture/money-and-wallet.md#single-writer-outbox) — the only honest answer is 202 with nothing attached (docs/architecture/http-contract.md#async-actions-return-202).");
    }
}
