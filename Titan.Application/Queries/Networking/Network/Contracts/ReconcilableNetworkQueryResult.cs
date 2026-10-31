using Titan.Domain.Aggregates.Network;

namespace Titan.Application.Queries.Networking.Network.Contracts;

// What the reconciler needs to match what Incus holds to a Titan network and to report only what changed:
// CurrentState and CurrentExistence are what Titan last recorded, so an unchanged observation sends nothing.
// Internal only — never on a customer path.
public record ReconcilableNetworkQueryResult(Guid Id, string ExternalId, string? CurrentState, string CurrentExistence)
{
    public static ReconcilableNetworkQueryResult From(ReconcilableNetwork n) =>
        new(n.Id, n.ExternalId, StatusName(n.CurrentState), n.CurrentExistence.ToString());

    private static string? StatusName(NetworkStatus? status)
    {
        if (status == null)
        {
            return null;
        }
        return status.Value.ToString();
    }
}
