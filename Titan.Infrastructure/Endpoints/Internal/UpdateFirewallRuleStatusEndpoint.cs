using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The old callback route, alive until rollout phase 6: a command without a requestId, and the reconciler of a worker
// from before the request routes, report here; the rest report on the request and observation routes
// (titan-ops architecture/resource-lifecycle.md#callback-routes).
public class UpdateFirewallRuleStatusEndpoint(UpdateFirewallRuleStatusCommandHandler handler, IConfiguration config)
    : Endpoint<UpdateFirewallRuleStatusBody>
{
    public override void Configure()
    {
        Patch("internal/networks/{networkId:guid}/firewall-rules/{ruleId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdateFirewallRuleStatusBody body, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid networkId = Route<Guid>("networkId");
        Guid ruleId = Route<Guid>("ruleId");
        await handler.HandleAsync(
            new UpdateFirewallRuleStatusCommand(networkId, ruleId, body.Report, body.Exists, body.State,
                body.ExternalRuleId, body.Reason, body.Detail),
            ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; UpdateFirewallRuleStatusCommand additionally carries the route ids.
public record UpdateFirewallRuleStatusBody(
    string Report,
    bool? Exists = null,
    string? State = null,
    string? ExternalRuleId = null,
    string? Reason = null,
    string? Detail = null);
