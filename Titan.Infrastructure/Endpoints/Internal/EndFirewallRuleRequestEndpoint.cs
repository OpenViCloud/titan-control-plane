using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a firewall rule request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes); the rule is a child, so its route sits under its
// network. 204 also when the rule is not waiting on that request.
public class EndFirewallRuleRequestEndpoint(EndFirewallRuleRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndFirewallRuleRequestBody>
{
    public override void Configure()
    {
        Patch("internal/networks/{networkId:guid}/firewall-rules/{ruleId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndFirewallRuleRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid networkId = Route<Guid>("networkId");
        Guid ruleId = Route<Guid>("ruleId");
        Guid requestId = Route<Guid>("requestId");
        await handler.HandleAsync(new EndFirewallRuleRequestCommand(
            networkId, ruleId, requestId, body.Outcome, body.ExternalId, body.Observed?.State, body.Reason,
            body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; EndFirewallRuleRequestCommand additionally carries the three route ids.
public record EndFirewallRuleRequestBody(
    string Outcome,
    string? ExternalId = null,
    FirewallRuleObservedBody? Observed = null,
    string? Reason = null,
    string? Detail = null);

// What the worker read of the rule in its network's ACL: the rule state word for word. Shared by the request report and
// the reconciler's observation.
public record FirewallRuleObservedBody(string? State = null);
