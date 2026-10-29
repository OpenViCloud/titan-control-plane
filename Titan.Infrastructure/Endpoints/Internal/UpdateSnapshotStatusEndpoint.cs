using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// Both ids ride the route (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record UpdateSnapshotStatusBody(
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

public class UpdateSnapshotStatusEndpoint(
    UpdateVmSnapshotStatusCommandHandler handler,
    IConfiguration config) : Endpoint<UpdateSnapshotStatusBody>
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/snapshots/{snapshotId:guid}/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdateSnapshotStatusBody body, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var vmId = Route<Guid>("vmId");
        var snapshotId = Route<Guid>("snapshotId");
        await handler.HandleAsync(
            new UpdateVmSnapshotStatusCommand(
                vmId, snapshotId, body.Report, body.Exists, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}
