using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Catalog.FunctionRuntime;
using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The runtime reconciler's observation of one function cluster (titan-ops architecture/resource-lifecycle.md#callback-routes):
// whether every Ready node holds the image. Ends no request.
public class ObserveFunctionRuntimeEndpoint(ObserveFunctionRuntimeCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveFunctionRuntimeBody>
{
    public override void Configure()
    {
        Patch("internal/function-runtimes/{runtimeId:guid}/clusters/{clusterId}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveFunctionRuntimeBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A body without exists would read as "gone" and record the image missing; nothing is written.
        if (body.Exists == null)
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        await handler.HandleAsync(
            new ObserveFunctionRuntimeCommand(Route<Guid>("runtimeId"), Route<string>("clusterId")!, body.Exists.Value), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command also carries the two route values. Exists is required; nullable so a body without
// it reaches the 400 above.
public record ObserveFunctionRuntimeBody(bool? Exists);
