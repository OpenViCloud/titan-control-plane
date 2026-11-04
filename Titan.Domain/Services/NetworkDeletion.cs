using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Domain.Services;

/// The one way a network is deleted: a network still carrying a VM or a load balancer is not
/// (titan-ops architecture/domain-services.md#cross-aggregate-lock). The rule spans the network and what sits on it, so it
/// is a domain service; Network.RequestDelete is internal, so no caller deletes without the check.
/// The caller loads the network with its row locked, so a create onto it waits for this request and then sees it.
public class NetworkDeletion(INetworkRepository networks)
{
    /// The request and the provider id the delete command takes, or null when the network never reached Incus and is
    /// gone here already.
    public async Task<RequestTicket?> RequestAsync(Network network, CancellationToken ct = default)
    {
        if (await networks.HasActiveVmsAsync(network.Id, ct))
        {
            throw new BusinessException("NETWORK_NOT_EMPTY", "Cannot delete a network that still has VMs.");
        }
        if (await networks.HasLiveLoadBalancersAsync(network.Id, ct))
        {
            throw new BusinessException("NETWORK_NOT_EMPTY", "Cannot delete a network that still has load balancers.");
        }

        // A network that never reached Incus is deleted by RequestDelete itself and gets no ticket; no rule can exist on it.
        return network.RequestDelete();
    }
}
