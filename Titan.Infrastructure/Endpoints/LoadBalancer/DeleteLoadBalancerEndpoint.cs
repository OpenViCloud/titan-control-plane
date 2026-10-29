using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

public class DeleteLoadBalancerEndpoint(DeleteLoadBalancerCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/load-balancers/{loadBalancerId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var loadBalancerId = Route<Guid>("loadBalancerId");
        await handler.HandleAsync(new DeleteLoadBalancerCommand(loadBalancerId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body
    }
}
