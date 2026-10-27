using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// The VM reconciler reads one zone's pool and compares it with this list. Same shape as
// ListReconcilableNetworksEndpoint, including the heartbeat.
public class ListReconcilableVmsEndpoint(
    ListReconcilableVmsQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableVmQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/vms");
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

        string? zoneId = ClusterQuery.From(HttpContext.Request);
        if (string.IsNullOrEmpty(zoneId))
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        List<ReconcilableVmQueryResult> rows = await handler.HandleAsync(new ListReconcilableVmsQuery(zoneId), ct);
        heartbeat.RecordPoll(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.VmKind, zoneId), DateTimeOffset.UtcNow);
        await SendAsync(rows, cancellation: ct);
    }
}
