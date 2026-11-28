using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Catalog.FunctionRuntime;
using Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// One runtime reconciler per function cluster, in the function worker (titan-ops features/ovc-function/design.md#runtimes).
public class ListReconcilableFunctionRuntimesEndpoint(
    ListReconcilableFunctionRuntimesQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableFunctionRuntimeQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/function-runtimes");
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

        List<ReconcilableFunctionRuntimeQueryResult> rows =
            await handler.HandleAsync(new ListReconcilableFunctionRuntimesQuery(clusterId), ct);
        await heartbeat.RecordPollAsync(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.FunctionRuntimeKind, clusterId), DateTimeOffset.UtcNow, ct);
        await SendAsync(rows, cancellation: ct);
    }
}
