using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Options;

namespace Titan.Infrastructure.Endpoints.Internal;

public record InternalClusterResponse(string Id, string Name);

// The clusters the worker reconciles, from the configuration (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-from-config).
// Behind X-Internal-Token: the worker does not depend on a public route for its own work.
public class ListInternalClustersEndpoint(ClusterOptions clusters, IConfiguration config)
    : EndpointWithoutRequest<List<InternalClusterResponse>>
{
    public override void Configure()
    {
        Get("internal/clusters");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }
        var configured = clusters.Clusters.Select(c => new InternalClusterResponse(c.Id, c.Name)).ToList();
        await SendAsync(configured, cancellation: ct);
    }
}
