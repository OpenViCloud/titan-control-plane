using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.VmSnapshots;

// {vmId} rides the route, so the BODY record carries only Name (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateSnapshotBody(string Name);

public class CreateSnapshotEndpoint(CreateVmSnapshotCommandHandler handler)
    : Endpoint<CreateSnapshotBody, CreateVmSnapshotCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/vms/{vmId:guid}/snapshots");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateVmSnapshotCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(CreateSnapshotBody body, CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        var result = await handler.HandleAsync(new CreateVmSnapshotCommand(vmId, body.Name), ct);
        await SendAsync(result, 202, ct);
    }
}
