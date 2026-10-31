using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;

namespace Titan.Application.Commands.Networking.Network;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation:
// observation converges). The create and delete commands report here when they finish, the network reconciler every
// cycle. Outcomes are recorded once: the aggregate says whether a report fulfilled the waiting request
// (titan-ops features/activity/design.md#outcome-source).
public class UpdateNetworkStatusCommandHandler(
    INetworkRepository repo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateNetworkStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before the network is loaded: a malformed report is the worker's bug, whatever the network.
        NetworkStatus? observedStatus = null;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                if (req.Exists == true)
                {
                    observedStatus = ParseIncusStatus(req.Status);
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(req.ExternalId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalId is required when report is Created.");
                }
                break;
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown network report: '{req.Report}'");
        }

        var network = await repo.FindByIdAsync(req.NetworkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
                if (observedStatus == null)
                {
                    network.MarkNotFound();
                }
                else
                {
                    bool createdNow = network.MarkObserved(observedStatus.Value);
                    if (createdNow)
                    {
                        outcome = "network.created";
                    }
                }
                break;
            case "Created":
                network.MarkCreated(req.ExternalId!, req.Cidr, req.Gateway);
                break;
            case "RequestFailed":
                network.MarkRequestFailed(req.ExternalId, req.Cidr, req.Gateway, req.Reason, req.Detail);
                break;
            case "Deleted":
                // The network takes its rules with it (titan-ops features/network/design.md#network-lifecycle) and names
                // the ones whose own delete was waiting: their own delete command will only find them already Deleted,
                // so their outcome is recorded now.
                NetworkDeletionOutcome deletion = network.MarkDeleted();
                if (deletion.FulfilledNetworkDelete)
                {
                    outcome = "network.deleted";
                }
                foreach (FirewallRuleEntity rule in deletion.RulesWhoseDeleteWasFulfilled)
                {
                    activity.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id,
                        FirewallRuleLabel.Describe(rule));
                }
                break;
        }

        if (outcome != null)
        {
            activity.StageOutcome(network.TenantId, outcome, "network", network.Id, network.Name.Value);
        }

        await repo.SaveAsync(ct);
    }

    // The worker sends the Incus status word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    // Matched by name, exactly: Enum.TryParse would also take "1" or "created".
    private static NetworkStatus ParseIncusStatus(string? status)
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
}
