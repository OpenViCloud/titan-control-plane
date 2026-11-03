using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The old callback route, alive until rollout phase 6: a command without a requestId, and the reconciler of a worker
// from before the request routes, report here; the rest report on the request and observation routes
// (titan-ops architecture/resource-lifecycle.md#callback-routes).
public class UpdateLoadBalancerStatusEndpoint(UpdateLoadBalancerStatusCommandHandler handler, IConfiguration config)
    : Endpoint<UpdateLoadBalancerStatusBody>
{
    public override void Configure()
    {
        Patch("internal/load-balancers/{loadBalancerId:guid}/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdateLoadBalancerStatusBody body, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid loadBalancerId = Route<Guid>("loadBalancerId");
        await handler.HandleAsync(
            new UpdateLoadBalancerStatusCommand(loadBalancerId, body.Report, body.Exists, body.ExternalId,
                body.IntentId, body.Reason, body.Detail),
            ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; UpdateLoadBalancerStatusCommand additionally carries the route id.
public record UpdateLoadBalancerStatusBody(
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    Guid? IntentId = null,
    string? Reason = null,
    string? Detail = null);
