using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Queries.Networking.Network.Contracts;

// What the reconciler needs to match what Incus holds to a Titan network and to report only what changed:
// CurrentState and CurrentExistence are what Titan last recorded, so an unchanged observation sends nothing.
// Internal only — never on a customer path.
public record ReconcilableNetworkQueryResult(Guid Id, string ExternalId, string? CurrentState, string CurrentExistence)
{
    // The repository lists only networks with a provider id, so a missing one here is a broken query, not a row to skip quietly.
    public static ReconcilableNetworkQueryResult From(NetworkAggregate n)
    {
        string externalId = n.ExternalId ?? throw new InvalidOperationException($"Network {n.Id} has no provider id and cannot be reconciled.");
        return new(n.Id, externalId, StatusName(n.CurrentState), n.CurrentExistence.ToString());
    }

    private static string? StatusName(NetworkStatus? status)
    {
        if (status == null)
        {
            return null;
        }
        return status.Value.ToString();
    }
}
