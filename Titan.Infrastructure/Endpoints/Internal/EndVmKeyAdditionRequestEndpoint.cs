using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VmKeyAddition;
using Titan.Application.Commands.Compute.VmKeyAddition.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a key addition operation ended (titan-ops architecture/resource-lifecycle.md#callback-routes);
// the addition is a child, so its route sits under its VM. 204 also when the addition is not waiting on that
// operation.
public class EndVmKeyAdditionRequestEndpoint(EndVmKeyAdditionRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndVmKeyAdditionRequestBody>
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/key-additions/{keyAdditionId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndVmKeyAdditionRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await handler.HandleAsync(new EndVmKeyAdditionRequestCommand(
            Route<Guid>("vmId"), Route<Guid>("keyAdditionId"), Route<Guid>("requestId"),
            body.Outcome, body.RestartNeeded, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the three route ids.
public record EndVmKeyAdditionRequestBody(
    string Outcome,
    bool? RestartNeeded = null,
    string? Reason = null,
    string? Detail = null);
