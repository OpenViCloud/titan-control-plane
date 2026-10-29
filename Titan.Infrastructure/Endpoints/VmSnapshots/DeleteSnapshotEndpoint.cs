using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.VmSnapshots;

public class DeleteSnapshotEndpoint(DeleteVmSnapshotCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/vms/{vmId:guid}/snapshots/{snapshotId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        var snapshotId = Route<Guid>("snapshotId");
        await handler.HandleAsync(new DeleteVmSnapshotCommand(vmId, snapshotId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body, not `{}`
    }
}
