using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Queries.Networking.Network.Contracts;

// What the reconciler needs to match what Incus holds to a Titan network and to report only what changed: what Titan last
// recorded, so an unchanged observation sends nothing. Internal only — never on a customer path. The worker contract keeps
// its two fields (titan-ops features/network/design.md#network-reconciler): CurrentState is the Incus status the network
// holds (InstanceStatus), CurrentExistence is read off the status and that one.
public record ReconcilableNetworkQueryResult(Guid Id, string ExternalId, string? CurrentState, string CurrentExistence)
{
    // The repository lists only networks with a provider id, so a missing one here is a broken query, not a row to skip quietly.
    public static ReconcilableNetworkQueryResult From(NetworkAggregate n)
    {
        string externalId = n.ExternalId ?? throw new InvalidOperationException($"Network {n.Id} has no provider id and cannot be reconciled.");
        return new(n.Id, externalId, StateName(n.InstanceStatus), ExistenceName(n));
    }

    // The values of CurrentExistence the worker compares with: NotFound stops it repeating an absent network, Exists with a
    // status stops it repeating a status Titan already has; anything else makes it report next cycle.
    private static string ExistenceName(NetworkAggregate n)
    {
        if (n.Status == NetworkStatus.NotFound)
        {
            return "NotFound";
        }
        if (n.InstanceStatus != null)
        {
            return "Exists";
        }
        return "NotCreated";
    }

    private static string? StateName(NetworkStatus? status)
    {
        if (status == null)
        {
            return null;
        }
        return status.Value.ToString();
    }
}
