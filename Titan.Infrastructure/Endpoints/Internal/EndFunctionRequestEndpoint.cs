using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The function worker reports that a function request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes): the route names the function and the request, the
// body says how it ended and the Knative Service the deploy made. 204 also when the function is not waiting on that
// request: the worker has nothing to retry.
public class EndFunctionRequestEndpoint(EndFunctionRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndFunctionRequestBody>
{
    public override void Configure()
    {
        Patch("internal/functions/{functionId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndFunctionRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await handler.HandleAsync(new EndFunctionRequestCommand(
            Route<Guid>("functionId"), Route<Guid>("requestId"), body.Outcome, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body (OpenViCloud/titan-function-worker:resource/function/report.go); EndFunctionRequestCommand
// additionally carries the two route ids.
public record EndFunctionRequestBody(
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
