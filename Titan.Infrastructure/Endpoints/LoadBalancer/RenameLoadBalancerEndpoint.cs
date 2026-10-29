using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

// Titan-side only, no provider command. 204 with no body (titan-ops architecture/http-contract.md#commands-return-no-data).
public class RenameLoadBalancerEndpoint(RenameLoadBalancerCommandHandler handler) : Endpoint<RenameLoadBalancerBody>
{
    public override void Configure()
    {
        Patch("v1/tenant/load-balancers/{loadBalancerId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(RenameLoadBalancerBody req, CancellationToken ct)
    {
        var loadBalancerId = Route<Guid>("loadBalancerId");
        await handler.HandleAsync(new RenameLoadBalancerCommand(loadBalancerId, req.Name), ct);
        await SendNoContentAsync(ct);
    }
}

// The JSON body; RenameLoadBalancerCommand also carries the route's id (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record RenameLoadBalancerBody(string Name);
