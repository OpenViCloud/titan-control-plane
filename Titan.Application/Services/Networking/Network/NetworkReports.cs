using Titan.Application.Abstractions;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using Titan.Application.Exceptions;

namespace Titan.Application.Services.Networking.Network;

/// <summary>
/// What every network and firewall rule callback handler does with a worker's report, in one place: parse the outcome
/// and the Incus words, and record the outcome of a request that ended once
/// (titan-ops features/activity/design.md#request-and-outcome-names). The new routes and the old ones share it, so they
/// cannot drift apart (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class NetworkReports
{
    private static readonly Dictionary<NetworkRequestKind, string> DoneAction = new()
    {
        [NetworkRequestKind.Create] = "network.created",
        [NetworkRequestKind.Delete] = "network.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<NetworkRequestKind, string> FailedAction = new()
    {
        [NetworkRequestKind.Delete] = "network.delete_failed",
    };

    private static readonly Dictionary<FirewallRuleRequestKind, string> RuleDoneAction = new()
    {
        [FirewallRuleRequestKind.Create] = "firewall_rule.created",
        [FirewallRuleRequestKind.Delete] = "firewall_rule.deleted",
    };

    private static readonly Dictionary<FirewallRuleRequestKind, string> RuleFailedAction = new()
    {
        [FirewallRuleRequestKind.Delete] = "firewall_rule.delete_failed",
    };

    public static RequestOutcome ParseOutcome(string outcome)
    {
        switch (outcome)
        {
            case "Done":
                return RequestOutcome.Done;
            case "Failed":
                return RequestOutcome.Failed;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown request outcome: '{outcome}'");
        }
    }

    // The worker sends the Incus status word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    // Matched by name, exactly: Enum.TryParse would also take "1" or "created".
    public static NetworkStatus ParseStatus(string? status)
    {
        foreach (NetworkStatus known in Enum.GetValues<NetworkStatus>())
        {
            if (known.ToString() == status)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown network status: '{status}'");
    }

    // The worker sends the Incus ACL rule state word for word; matched by name, exactly, like the status.
    public static FirewallRuleState ParseState(string? state)
    {
        foreach (FirewallRuleState known in Enum.GetValues<FirewallRuleState>())
        {
            if (known.ToString() == state)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown firewall rule state: '{state}'");
    }

    /// Records the ended request's outcome once, and the rules a network's delete took whose own delete was waiting.
    public static void RecordEnded(NetworkAggregate network, NetworkRequestEnded ended, RequestOutcome outcome,
        IActivityRecorder activity)
    {
        Dictionary<NetworkRequestKind, string> actions = outcome == RequestOutcome.Done ? DoneAction : FailedAction;
        if (actions.TryGetValue(ended.Request.Kind, out string? action))
        {
            activity.StageOutcome(network.TenantId, action, "network", network.Id, network.Name.Value);
        }
        foreach (FirewallRuleEntity rule in ended.RulesWhoseDeleteEnded)
        {
            activity.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id,
                FirewallRuleLabel.Describe(rule));
        }
    }

    public static void RecordRuleEnded(FirewallRuleEntity rule, PendingRequest<FirewallRuleRequestKind> ended,
        RequestOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<FirewallRuleRequestKind, string> actions = outcome == RequestOutcome.Done ? RuleDoneAction : RuleFailedAction;
        if (actions.TryGetValue(ended.Kind, out string? action))
        {
            activity.StageOutcome(rule.TenantId, action, "firewall_rule", rule.Id, FirewallRuleLabel.Describe(rule));
        }
    }
}
