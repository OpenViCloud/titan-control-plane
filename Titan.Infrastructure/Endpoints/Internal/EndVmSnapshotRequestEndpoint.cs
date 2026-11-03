using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a VM snapshot request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes); the snapshot is a child, so its route sits under its
// VM. 204 also when the snapshot is not waiting on that request.
public class EndVmSnapshotRequestEndpoint(EndVmSnapshotRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndVmSnapshotRequestBody>
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/snapshots/{snapshotId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndVmSnapshotRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await handler.HandleAsync(new EndVmSnapshotRequestCommand(
            Route<Guid>("vmId"), Route<Guid>("snapshotId"), Route<Guid>("requestId"),
            body.Outcome, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the three route ids.
public record EndVmSnapshotRequestBody(
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
