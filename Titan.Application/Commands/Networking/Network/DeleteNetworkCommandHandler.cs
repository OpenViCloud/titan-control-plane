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
        if (network.IsDeletedOrDeleting)
        {
            return;
        }

        // Null: the create failed before Incus had it, so the network is gone here, the tenant can reclaim the name,
        // and no command is enqueued.
        OperationTicket? ticket = await deletion.DeleteAsync(network, ct);
        activity.Stage("network.delete_requested", "network", network.Id, network.Name.Value); // atomic with the save
        if (ticket is null)
        {
            // Never provisioned: the request and its outcome happen here at once (titan-ops architecture/intent-and-observation.md#intent-has-identity).
            activity.StageOutcome(network.TenantId, "network.deleted", "network", network.Id, network.Name.Value);
            await repo.SaveAsync(ct);
            return;
        }

        await commands.EnqueueNetworkDeleteAsync(network, ticket.OperationId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(network.TenantId), ct);
        // The rules stay as Incus holds them: the delete command takes the ACL with the network, and its Done ends
        // them (titan-ops features/network/design.md#network-lifecycle).
        await repo.SaveAsync(ct);
    }
}
