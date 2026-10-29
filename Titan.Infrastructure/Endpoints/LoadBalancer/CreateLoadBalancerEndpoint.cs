using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Infrastructure.Auth;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

// 202: the load balancer waits for Incus (titan-ops architecture/http-contract.md#create-returns-id).
public class CreateLoadBalancerEndpoint(CreateLoadBalancerCommandHandler handler)
    : Endpoint<CreateLoadBalancerCommand, CreateLoadBalancerCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/load-balancers");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateLoadBalancerCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(CreateLoadBalancerCommand cmd, CancellationToken ct)
    {
        var result = await handler.HandleAsync(cmd, ct);
        await SendAsync(result, 202, ct);
    }
}
