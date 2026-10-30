using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Queries.Admin.Network.Contracts;

// Admin-facing — carries TenantId (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read) and the raw
// failure detail the tenant never sees (titan-ops architecture/desired-and-current.md#request-failure). States follow
// titan-ops architecture/desired-and-current.md#provider-state-is-verbatim.
public record AdminNetworkQueryResult(
    Guid Id,
    Guid TenantId,
    string Name,
    string ClusterId,
    string? Cidr,
    string? CurrentState,
    string CurrentExistence,
    string DesiredExistence,
    string? Transition,
    string? RequestFailureReason,
    string? RequestFailureDetail,
    DateTimeOffset? RequestFailedAt,
    DateTimeOffset CreatedAt,
    string? PublicIpv6Subnet)
{
    public static AdminNetworkQueryResult From(NetworkAggregate n) =>
        new(n.Id, n.TenantId, n.Name, n.ClusterId, n.Cidr, StatusName(n.CurrentState), n.CurrentExistence.ToString(),
            n.DesiredExistence.ToString(), TransitionName(n.Transition),
            n.RequestFailureReason, n.RequestFailureDetail, n.RequestFailedAt, n.CreatedAt, n.PublicIpv6Subnet);

    private static string? StatusName(NetworkStatus? status)
    {
        if (status == null)
        {
            return null;
        }
        return status.Value.ToString();
    }

    private static string? TransitionName(NetworkTransition? transition)
    {
        if (transition == null)
        {
            return null;
        }
        return transition.Value.ToString();
    }
}
