using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.VmSnapshot;
using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler looks for each snapshot on its VM (one VM info per VM). Same shape as
// ListReconcilableNetworksEndpoint, including the heartbeat.
public class ListReconcilableVmSnapshotsEndpoint(
    ListReconcilableVmSnapshotsQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableVmSnapshotQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/vm-snapshots");
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

        List<ReconcilableVmSnapshotQueryResult> rows = await handler.HandleAsync(new ListReconcilableVmSnapshotsQuery(clusterId), ct);
        heartbeat.RecordPoll(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.VmSnapshotKind, clusterId), DateTimeOffset.UtcNow);
        await SendAsync(rows, cancellation: ct);
    }
}
