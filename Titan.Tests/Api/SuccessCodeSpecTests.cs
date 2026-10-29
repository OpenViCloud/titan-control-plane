using System.Text.RegularExpressions;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Titan.Tests.Api;

// titan-ops features/public-api/design.md#success-codes: every operation of the public spec declares the one 2xx its
// endpoint sends, with a body exactly when the endpoint sends one. WHICH operations comes from the running host's
// EndpointDataSource (filtered like the spec); the "sends" half is textual — it reads the endpoint's own file, so it
// is silent about a status set by hand on Response.StatusCode.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class SuccessCodeSpecTests(OpenApiSpecTests.ApiFactory factory) : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    // Pinned to the operation count of api/openapi-v1.json, so an empty or shrunken scan fails instead of passing.
    private const int PublicOperations = 60;

    private static readonly string EndpointsDir =
        Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "Endpoints");

    private static readonly (Regex Call, int? Code, bool Body)[] SuccessSends =
    [
        (new Regex(@"\bSendNoContentAsync\("), 204, false),
        (new Regex(@"\bStatus202Accepted\b"), 202, false),
        (new Regex(@"\bSendAsync\([^;]*,\s*(2\d\d)\s*,"), null, true),
        (new Regex(@"\bSendAsync\([^;]*\bcancellation:"), 200, true),
        (new Regex(@"\bSendOkAsync\(\s*ct\s*\)"), 200, false),
        (new Regex(@"\bSendOkAsync\((?!\s*ct\s*\))"), 200, true),
    ];

    private static bool IsPublic(string route) =>
        route.StartsWith("v1/", StringComparison.OrdinalIgnoreCase)
        && !route.StartsWith("v1/tenants", StringComparison.OrdinalIgnoreCase)
        && !route.StartsWith("v1/admin", StringComparison.OrdinalIgnoreCase);

    private static HashSet<(int Code, bool Body)> SentBy(string source)
    {
        var sent = new HashSet<(int, bool)>();
        foreach (var (call, code, body) in SuccessSends)
        {
            foreach (Match match in call.Matches(source))
            {
                int status = code ?? int.Parse(match.Groups[1].Value);
                sent.Add((status, body));
            }
        }
        return sent;
    }

    private static string SourceOf(Type endpoint)
    {
        var files = Directory.EnumerateFiles(EndpointsDir, $"{endpoint.Name}.cs", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count == 1,
            $"Expected exactly one source file named {endpoint.Name}.cs under {EndpointsDir}, found {files.Count}.");
        return File.ReadAllText(files[0]);
    }

    [Fact]
    public void EveryPublicOperation_DeclaresTheSuccessCodeItSends()
    {
        factory.CreateClient(); // starts the host so the routing table exists
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        var operations = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<EndpointDefinition>() is not null)
            .Where(e => IsPublic(e.RoutePattern.RawText ?? ""))
            .Where(e => e.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true)
            .ToList();

        Assert.Equal(PublicOperations, operations.Count);

        var offenders = new List<string>();
        foreach (var endpoint in operations)
        {
            var def = endpoint.Metadata.GetMetadata<EndpointDefinition>()!;
            var verbs = string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []);
            var name = $"{verbs} {endpoint.RoutePattern.RawText}";

            var sent = SentBy(SourceOf(def.EndpointType));
            if (sent.Count != 1)
            {
                offenders.Add($"{name}: sends {sent.Count} distinct success answers ({string.Join(", ", sent)})");
                continue;
            }
            var (code, body) = sent.Single();

            // One entry per content type, so one code can appear twice.
            var declared = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Where(m => m.StatusCode >= 200 && m.StatusCode < 300)
                .GroupBy(m => m.StatusCode)
                .ToList();
            if (declared.Count != 1)
            {
                offenders.Add($"{name}: declares {declared.Count} success codes ({string.Join(", ", declared.Select(d => d.Key))}), sends {code}");
                continue;
            }
            int declaredCode = declared[0].Key;
            var bodyTypes = declared[0]
                .Select(m => m.Type)
                .Where(t => t != null && t != typeof(void))
                .Distinct()
                .ToList();
            bool declaresBody = bodyTypes.Count > 0;

            if (declaredCode != code)
                offenders.Add($"{name}: declares {declaredCode}, sends {code}");
            if (declaresBody != body)
                offenders.Add($"{name}: declares {(declaresBody ? "a body" : "no body")}, sends {(body ? "a body" : "none")}");
            if (body && declaresBody && (bodyTypes.Count != 1 || bodyTypes[0] != def.ResDtoType))
                offenders.Add($"{name}: declares body {string.Join("/", bodyTypes.Select(t => t!.Name))}, sends {def.ResDtoType.Name}");
        }

        Assert.True(offenders.Count == 0,
            "The public spec states a success answer the endpoint does not send — a client generated from it "
            + "reads the wrong branch (docs/features/public-api/design.md#success-codes): " + string.Join(" | ", offenders));
    }
}
