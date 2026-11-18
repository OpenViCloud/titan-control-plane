using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Compute.VmKeyAddition;
using Titan.Application.Commands.Compute.VmKeyAddition.Contracts;

namespace Titan.Infrastructure.Endpoints.VmKeyAdditions;

// {vmId} rides the route, so the BODY record carries only SshKeyIds (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateKeyAdditionBody(List<Guid> SshKeyIds);

public class CreateKeyAdditionEndpoint(CreateVmKeyAdditionCommandHandler handler)
    : Endpoint<CreateKeyAdditionBody, CreateVmKeyAdditionCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/vms/{vmId:guid}/key-additions");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateVmKeyAdditionCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(CreateKeyAdditionBody body, CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        var result = await handler.HandleAsync(new CreateVmKeyAdditionCommand(vmId, body.SshKeyIds), ct);
        await SendAsync(result, 202, ct);
    }
}
