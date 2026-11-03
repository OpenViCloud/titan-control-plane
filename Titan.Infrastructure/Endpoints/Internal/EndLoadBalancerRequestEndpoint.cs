using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a load balancer request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes): the route names the load balancer and the request, the
// body says how it ended and the VIP when the worker found it. 204 also when the load balancer is not waiting on that
// request: the worker has nothing to retry.
public class EndLoadBalancerRequestEndpoint(EndLoadBalancerRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndLoadBalancerRequestBody>
{
    public override void Configure()
    {
        Patch("internal/load-balancers/{loadBalancerId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndLoadBalancerRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid loadBalancerId = Route<Guid>("loadBalancerId");
        Guid requestId = Route<Guid>("requestId");
        await handler.HandleAsync(new EndLoadBalancerRequestCommand(
            loadBalancerId, requestId, body.Outcome, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; EndLoadBalancerRequestCommand additionally carries the two route ids.
public record EndLoadBalancerRequestBody(
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
