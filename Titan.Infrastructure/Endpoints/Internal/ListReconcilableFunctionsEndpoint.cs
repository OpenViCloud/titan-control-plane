using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// One function reconciler per function cluster, in the function worker
// (titan-ops features/ovc-function/design.md#function-lifecycle).
public class ListReconcilableFunctionsEndpoint(
    ListReconcilableFunctionsQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableFunctionQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/functions");
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

        List<ReconcilableFunctionQueryResult> rows = await handler.HandleAsync(new ListReconcilableFunctionsQuery(clusterId), ct);
        await heartbeat.RecordPollAsync(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.FunctionKind, clusterId), DateTimeOffset.UtcNow, ct);
        await SendAsync(rows, cancellation: ct);
    }
}
