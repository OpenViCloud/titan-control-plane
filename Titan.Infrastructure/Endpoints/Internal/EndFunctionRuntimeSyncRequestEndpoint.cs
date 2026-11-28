using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Catalog.FunctionRuntime;
using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The function worker reports that a runtime sync's command in one function cluster ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes); a runtime's state lives per cluster, so the route sits
// under the cluster, as the template's. 204 also when the cluster is not waiting on that request.
public class EndFunctionRuntimeSyncRequestEndpoint(EndFunctionRuntimeSyncRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndFunctionRuntimeSyncRequestBody>
{
    public override void Configure()
    {
        Patch("internal/function-runtimes/{runtimeId:guid}/clusters/{clusterId}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndFunctionRuntimeSyncRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await handler.HandleAsync(new EndFunctionRuntimeSyncRequestCommand(
            Route<Guid>("runtimeId"), Route<string>("clusterId")!, Route<Guid>("requestId"),
            body.Outcome, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body (OpenViCloud/titan-function-worker:resource/runtime/report.go RequestEnd); the command also
// carries the three route values.
public record EndFunctionRuntimeSyncRequestBody(
    string Outcome,
    string? Reason = null,
    string? Detail = null);
