using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one VM snapshot (titan-ops architecture/resource-lifecycle.md#callback-routes): whether
// its instance on Incus has it. Writes current only.
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

        await handler.HandleAsync(
            new ObserveVmSnapshotCommand(Route<Guid>("vmId"), Route<Guid>("snapshotId"), body.Exists), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the two route ids.
public record ObserveVmSnapshotBody(bool Exists);
