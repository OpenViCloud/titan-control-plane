using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The function reconciler's observation of one function (titan-ops architecture/resource-lifecycle.md#callback-routes):
// whether its Knative Service is in the tenant's namespace. Ends no request.
public class ObserveFunctionEndpoint(ObserveFunctionCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveFunctionBody>
{
    public override void Configure()
    {
        Patch("internal/functions/{functionId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveFunctionBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A body without exists would read as "gone" and record the Service missing; nothing is written.
        if (body.Exists == null)
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        await handler.HandleAsync(new ObserveFunctionCommand(Route<Guid>("functionId"), body.Exists.Value), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; ObserveFunctionCommand additionally carries the route id. Exists is required; nullable so a
// body without it reaches the 400 above.
public record ObserveFunctionBody(bool? Exists);
