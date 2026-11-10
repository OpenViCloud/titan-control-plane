using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one VM snapshot (titan-ops architecture/resource-lifecycle.md#callback-routes): whether
// its instance on Incus has it. Ends no request.
public class ObserveVmSnapshotEndpoint(ObserveVmSnapshotCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveVmSnapshotBody>
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/snapshots/{snapshotId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveVmSnapshotBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A body without exists would read as "gone" and record the resource missing; nothing is written.
        if (body.Exists == null)
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        await handler.HandleAsync(
            new ObserveVmSnapshotCommand(Route<Guid>("vmId"), Route<Guid>("snapshotId"), body.Exists.Value), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the two route ids.
// Exists is required; nullable so a body without it reaches the 400 above.
public record ObserveVmSnapshotBody(bool? Exists);
