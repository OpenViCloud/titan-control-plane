using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.Network;

public class DeleteNetworkCommandHandler(
    INetworkRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteNetworkCommand command, CancellationToken ct = default)
    {
        var networkId = command.NetworkId;

        var network = await NetworkOwnership.RequireNetworkAsync(repo, currentUser.RequireTenantId(), networkId, ct);

        // The aggregate refuses a repeat, so it is recognised here first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): this delete was already requested, or
        // the network is already deleted. A repeat answers like the first request: nothing recorded, nothing sent.
        bool deleteIsWaiting = network.Transition == NetworkTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || network.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        if (await repo.HasActiveVmsAsync(networkId, ct))
            throw new BusinessException("NETWORK_NOT_EMPTY", "Cannot delete a network that still has VMs.");
        if (await repo.HasLiveLoadBalancersAsync(networkId, ct))
            throw new BusinessException("NETWORK_NOT_EMPTY", "Cannot delete a network that still has load balancers.");

        // The create failed before Incus had it — nothing to clean up on Incus, and no rule can exist on a network
        // that never reached Created. Delete locally so the tenant can reclaim the name, and enqueue no command.
        bool neverReachedTheProvider = network.ExternalId is null && network.Transition == null;
        if (neverReachedTheProvider)
        {
            network.DeleteLocally();
            // Never provisioned: the request and its outcome happen here at once (titan-ops architecture/intent-and-observation.md#intent-has-identity).
            activity.Stage("network.delete_requested", "network", network.Id, network.Name.Value); // atomic with DeleteLocally
            activity.StageOutcome(network.TenantId, "network.deleted", "network", network.Id, network.Name.Value);
            await repo.SaveAsync(ct);
            return;
        }

        // RequestDelete validates AND yields the provider id: no `network.ExternalId!` below.
        string externalNetworkId = network.RequestDelete();

        activity.Stage("network.delete_requested", "network", network.Id, network.Name.Value); // atomic with RequestDelete
        await commands.EnqueueNetworkDeleteAsync(
            network.Id, externalNetworkId, TenantAggregate.ProjectNameFor(network.TenantId), network.ClusterId, ct);
        // The rules stay as Incus holds them: the delete command takes the ACL with the network, and its Deleted
        // report marks them (titan-ops features/network/design.md#network-lifecycle).
        await repo.SaveAsync(ct);
    }
}
