using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.LoadBalancer;
using Titan.Application.Queries.Compute.LoadBalancer.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// Load balancers of a cluster, matched against what Incus holds. Same shape as ListReconcilableNetworksEndpoint,
// including the heartbeat.
public class ListReconcilableLoadBalancersEndpoint(
    ListReconcilableLoadBalancersQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableLoadBalancerQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/load-balancers");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        string? clusterId = ClusterQuery.From(HttpContext.Request);
        if (string.IsNullOrEmpty(clusterId))
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        var rows = await handler.HandleAsync(new ListReconcilableLoadBalancersQuery(clusterId), ct);
        heartbeat.RecordPoll(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.LoadBalancerKind, clusterId), DateTimeOffset.UtcNow);
        await SendAsync(rows, cancellation: ct);
    }
}
