using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one load balancer (titan-ops architecture/resource-lifecycle.md#callback-routes):
// whether its VIP is on its network on Incus. Ends no request.
public class ObserveLoadBalancerEndpoint(ObserveLoadBalancerCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveLoadBalancerBody>
{
    public override void Configure()
    {
        Patch("internal/load-balancers/{loadBalancerId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveLoadBalancerBody body, CancellationToken ct)
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

        Guid loadBalancerId = Route<Guid>("loadBalancerId");
        await handler.HandleAsync(new ObserveLoadBalancerCommand(loadBalancerId, body.Exists.Value), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; ObserveLoadBalancerCommand additionally carries the route id.
// Exists is required; nullable so a body without it reaches the 400 above.
public record ObserveLoadBalancerBody(bool? Exists);
