using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Networking.Network;
using Titan.Application.Queries.Networking.Network.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// A network in this list that Incus does not hold is reported not found (titan-ops features/network/design.md#network-lifecycle);
// CurrentState and CurrentExistence come along so an unchanged observation sends nothing.
//
// Each call is the reconciler's heartbeat, recorded here rather than in the handler: it is an operational
// signal about the caller, not a fact about networks.
public class ListReconcilableNetworksEndpoint(
    ListReconcilableNetworksQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableNetworkQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/networks");
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

        List<ReconcilableNetworkQueryResult> networks = await handler.HandleAsync(new ListReconcilableNetworksQuery(clusterId), ct);
        heartbeat.RecordPoll(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.NetworkKind, clusterId), DateTimeOffset.UtcNow);
        await SendAsync(networks, cancellation: ct);
    }
}
