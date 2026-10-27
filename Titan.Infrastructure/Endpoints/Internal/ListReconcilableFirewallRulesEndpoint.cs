using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Networking.FirewallRule;
using Titan.Application.Queries.Networking.FirewallRule.Contracts;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

// Firewall rules of a zone, matched against what the provider holds. Same shape as
// ListReconcilableNetworksEndpoint, including the heartbeat.
public class ListReconcilableFirewallRulesEndpoint(
    ListReconcilableFirewallRulesQueryHandler handler,
    ReconcilerHeartbeat heartbeat,
    IConfiguration config)
    : EndpointWithoutRequest<List<ReconcilableFirewallRuleQueryResult>>
{
    public override void Configure()
    {
        Get("internal/reconcile/firewall-rules");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        string? zoneId = ClusterQuery.From(HttpContext.Request);
        if (string.IsNullOrEmpty(zoneId))
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        List<ReconcilableFirewallRuleQueryResult> rows = await handler.HandleAsync(new ListReconcilableFirewallRulesQuery(zoneId), ct);
        heartbeat.RecordPoll(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.FirewallKind, zoneId), DateTimeOffset.UtcNow);
        await SendAsync(rows, cancellation: ct);
    }
}
