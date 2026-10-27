using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.Volume;
using Titan.Application.Queries.Compute.Volume.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// Volumes and volume snapshots of a cluster, matched against what the provider holds. Same shape as
// ListReconcilableNetworksEndpoint, including the heartbeat.
public class ListReconcilableStorageEndpoint(
    ListReconcilableStorageQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<ReconcilableStorageQueryResult>
{
    public override void Configure()
    {
        Get("internal/reconcile/volumes");
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

        ReconcilableStorageQueryResult storage = await handler.HandleAsync(new ListReconcilableStorageQuery(clusterId), ct);
        heartbeat.RecordPoll(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.VolumeKind, clusterId), DateTimeOffset.UtcNow);
        await SendAsync(storage, cancellation: ct);
    }
}
