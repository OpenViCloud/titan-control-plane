using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

// The whole configuration in one PUT, applied by the worker in one Incus write; 204 with no body even though it
// is asynchronous (titan-ops architecture/http-contract.md#commands-return-no-data).
public class UpdateLoadBalancerEndpoint(UpdateLoadBalancerCommandHandler handler) : Endpoint<UpdateLoadBalancerBody>
{
    public override void Configure()
    {
        Put("v1/tenant/load-balancers/{loadBalancerId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(UpdateLoadBalancerBody body, CancellationToken ct)
    {
        var loadBalancerId = Route<Guid>("loadBalancerId");
        await handler.HandleAsync(new UpdateLoadBalancerCommand(loadBalancerId, body.Listeners, body.Backends), ct);
        await SendNoContentAsync(ct);
    }
}

// The JSON body; UpdateLoadBalancerCommand additionally carries the route's id
// (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record UpdateLoadBalancerBody(List<LoadBalancerListenerInput> Listeners, List<LoadBalancerBackendInput> Backends);
