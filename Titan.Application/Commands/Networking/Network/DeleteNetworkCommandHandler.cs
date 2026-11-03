using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.Network;

public class DeleteNetworkCommandHandler(
    INetworkRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    NetworkDeletion deletion)
{
    public async Task HandleAsync(DeleteNetworkCommand command, CancellationToken ct = default)
    {
        var networkId = command.NetworkId;

        // Locked, so a VM or load balancer create onto this network waits for this request and then sees it
        // (titan-ops architecture/domain-services.md#cross-aggregate-lock).
        var network = await NetworkOwnership.RequireNetworkForUpdateAsync(repo, currentUser.RequireTenantId(), networkId, ct);

        // The aggregate refuses a repeat, so it is recognised here first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): this delete was already requested, or
        // the network is already deleted. A repeat answers like the first request: nothing recorded, nothing sent.
        bool deleteIsWaiting = network.Transition == NetworkTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || network.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // Null: the create failed before Incus had it, so the network is gone here, the tenant can reclaim the name,
        // and no command is enqueued.
        RequestTicket? ticket = await deletion.RequestAsync(network, ct);
        if (ticket is null)
        {
            // Never provisioned: the request and its outcome happen here at once (titan-ops architecture/intent-and-observation.md#intent-has-identity).
            activity.Stage("network.delete_requested", "network", network.Id, network.Name.Value); // atomic with the local delete
            activity.StageOutcome(network.TenantId, "network.deleted", "network", network.Id, network.Name.Value);
            await repo.SaveAsync(ct);
            return;
        }

        activity.Stage("network.delete_requested", "network", network.Id, network.Name.Value); // atomic with the delete request
        await commands.EnqueueNetworkDeleteAsync(network.Id, ticket.RequestId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(network.TenantId), network.ClusterId, ct);
        // The rules stay as Incus holds them: the delete command takes the ACL with the network, and its Done ends
        // them (titan-ops features/network/design.md#network-lifecycle).
        await repo.SaveAsync(ct);
    }
}
