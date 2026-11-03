using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one firewall rule (titan-ops architecture/resource-lifecycle.md#callback-routes): whether
// its network's ACL has it and its state. Writes current only.
public class ObserveFirewallRuleEndpoint(ObserveFirewallRuleCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveFirewallRuleBody>
{
    public override void Configure()
    {
        Patch("internal/networks/{networkId:guid}/firewall-rules/{ruleId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveFirewallRuleBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid networkId = Route<Guid>("networkId");
        Guid ruleId = Route<Guid>("ruleId");
        await handler.HandleAsync(new ObserveFirewallRuleCommand(networkId, ruleId, body.Exists, body.Observed?.State), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; ObserveFirewallRuleCommand additionally carries the two route ids.
public record ObserveFirewallRuleBody(bool Exists, FirewallRuleObservedBody? Observed = null);
