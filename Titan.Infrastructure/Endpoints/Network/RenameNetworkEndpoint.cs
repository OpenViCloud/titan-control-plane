using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;

namespace Titan.Infrastructure.Endpoints.Network;

// CP-only metadata, no provider command. 204 with no body (titan-ops architecture/http-contract.md#commands-return-no-data).
public class RenameNetworkEndpoint(RenameNetworkCommandHandler handler) : Endpoint<RenameNetworkRequest>
{
    public override void Configure()
    {
        Patch("v1/tenant/networks/{networkId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(RenameNetworkRequest req, CancellationToken ct)
    {
        var networkId = Route<Guid>("networkId");
        await handler.HandleAsync(new RenameNetworkCommand(networkId, req.Name), ct);
        await SendNoContentAsync(ct);
    }
}

// The JSON body; RenameNetworkCommand additionally carries the route's network id (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record RenameNetworkRequest(string Name);
