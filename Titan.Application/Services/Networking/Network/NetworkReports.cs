using Titan.Application.Abstractions;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Services;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using Titan.Application.Exceptions;

namespace Titan.Application.Services.Networking.Network;

/// <summary>
/// What every network and firewall rule callback handler does with a worker's report, in one place: parse the outcome
/// and the Incus words, and record the outcome of a request that ended once
/// (titan-ops features/activity/design.md#request-and-outcome-names). The request and observation routes share it, so they
/// cannot drift apart (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class NetworkReports
{
    private static readonly Dictionary<NetworkStatus, string> DoneAction = new()
    {
        [NetworkStatus.Creating] = "network.created",
        [NetworkStatus.Deleting] = "network.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<NetworkStatus, string> FailedAction = new()
    {
        [NetworkStatus.Deleting] = "network.delete_failed",
    };

    private static readonly Dictionary<FirewallRuleStatus, string> RuleDoneAction = new()
    {
        [FirewallRuleStatus.Creating] = "firewall_rule.created",
        [FirewallRuleStatus.Deleting] = "firewall_rule.deleted",
    };

    private static readonly Dictionary<FirewallRuleStatus, string> RuleFailedAction = new()
    {
        [FirewallRuleStatus.Deleting] = "firewall_rule.delete_failed",
    };


    // The worker sends the Incus status word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    // Matched by name, exactly: Enum.TryParse would also take "1" or "created". Only the five words of Incus pass: Creating,
    // NotFound and the like are Titan's, never a worker's.
    public static NetworkStatus ParseStatus(string? status)
    {
        foreach (NetworkStatus known in Enum.GetValues<NetworkStatus>())
        {
            if (NetworkAggregate.IsIncusStatus(known) && known.ToString() == status)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown network status: '{status}'");
    }

    // The worker sends the Incus ACL rule state word for word; matched by name, exactly, like the status.
    public static FirewallRuleStatus ParseState(string? state)
    {
        foreach (FirewallRuleStatus known in Enum.GetValues<FirewallRuleStatus>())
        {
            if (FirewallRuleEntity.IsIncusStatus(known) && known.ToString() == state)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown firewall rule state: '{state}'");
    }

    /// Ends the network's waiting operation with a Done through NetworkRoutes, so a route the network lets go of goes
    /// back in the same save, and records its outcome once, with the rules a delete took along whose own delete was
    /// waiting. Null when the report names no waiting operation: nothing is recorded.
    public static async Task<NetworkStatus?> FinishOperationAsync(NetworkRoutes routes,
        NetworkAggregate network, Guid operationId, string? externalId, NetworkStatus? status, string? cidr, string? gateway,
        IActivityRecorder activity, CancellationToken ct)
    {
        // Read before the network's delete Done takes every rule along: the ones waiting on their own delete get its
        // outcome here, once, since their command will only find them gone.
        List<FirewallRuleEntity> rulesWaitingOnTheirDelete = new();
        foreach (FirewallRuleEntity rule in network.FirewallRules)
        {
            if (rule.IsDeletedOrDeleting && !rule.IsDeleted)
            {
                rulesWaitingOnTheirDelete.Add(rule);
            }
        }

        NetworkStatus? ended = await routes.FinishOperationAsync(network, operationId, externalId,
            status: status, cidr: cidr, gateway: gateway, ct);
        if (ended == null)
        {
            return null;
        }
        if (DoneAction.TryGetValue(ended.Value, out string? action))
        {
            activity.StageOutcome(network.TenantId, action, "network", network.Id, network.Name.Value);
        }
        if (ended == NetworkStatus.Deleting)
        {
            foreach (FirewallRuleEntity rule in rulesWaitingOnTheirDelete)
            {
                activity.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id,
                    FirewallRuleLabel.Describe(rule));
            }
        }
        return ended;
    }

    /// Ends the network's waiting operation with a give-up through NetworkRoutes, so a /64 the network lets go of goes
    /// back with its route in the same save, and records its outcome once. Null when the report names no waiting
    /// operation: nothing is recorded.
    public static async Task<NetworkStatus?> FailOperationAsync(NetworkRoutes routes,
        NetworkAggregate network, Guid operationId, string? externalId, NetworkStatus? status, string? cidr, string? gateway,
        string? reason, string? detail, IActivityRecorder activity, CancellationToken ct)
    {
        NetworkStatus? ended = await routes.FailOperationAsync(network, operationId, externalId,
            status: status, cidr: cidr, gateway: gateway, reason: reason, detail: detail, ct);
        if (ended == null)
        {
            return null;
        }
        if (FailedAction.TryGetValue(ended.Value, out string? action))
        {
            activity.StageOutcome(network.TenantId, action, "network", network.Id, network.Name.Value);
        }
        return ended;
    }

    /// Ends the rule's waiting operation with the report, the status the rule waits in saying which report it is, and
    /// records its outcome once. False when the report names no waiting operation: nothing is recorded.
    public static bool EndRuleOperation(NetworkAggregate network, FirewallRuleEntity rule, OperationOutcome outcome,
        Guid operationId, string? externalId, FirewallRuleStatus? state, string? reason, string? detail,
        IActivityRecorder activity)
    {
        FirewallRuleStatus waiting = rule.Status;
        bool ended;
        switch (waiting)
        {
            case FirewallRuleStatus.Creating:
                ended = outcome == OperationOutcome.Done
                    ? network.FirewallRuleCreateSucceeded(rule.Id, operationId, externalId, state)
                    : network.FirewallRuleCreateFailed(rule.Id, operationId, externalId, state, reason, detail);
                break;
            case FirewallRuleStatus.Deleting:
                ended = outcome == OperationOutcome.Done
                    ? network.FirewallRuleDeleteSucceeded(rule.Id, operationId)
                    : network.FirewallRuleDeleteFailed(rule.Id, operationId, state, reason, detail);
                break;
            default:
                ended = false;
                break;
        }
        if (ended)
        {
            RecordRuleEnded(rule, waiting, outcome, activity);
        }
        return ended;
    }

    private static void RecordRuleEnded(FirewallRuleEntity rule, FirewallRuleStatus ended,
        OperationOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<FirewallRuleStatus, string> actions = outcome == OperationOutcome.Done ? RuleDoneAction : RuleFailedAction;
        if (actions.TryGetValue(ended, out string? action))
        {
            activity.StageOutcome(rule.TenantId, action, "firewall_rule", rule.Id, FirewallRuleLabel.Describe(rule));
        }
    }
}
