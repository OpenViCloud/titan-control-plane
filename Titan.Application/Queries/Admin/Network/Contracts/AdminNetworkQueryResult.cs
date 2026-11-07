using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Queries.Admin.Network.Contracts;

// Admin-facing — carries TenantId (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read) and the raw
// failure detail the tenant never sees (titan-ops architecture/desired-and-current.md#request-failure). Status is the one
// status of the network (titan-ops features/network/design.md#network-lifecycle).
public record AdminNetworkQueryResult(
    Guid Id,
    Guid TenantId,
    string Name,
    string ClusterId,
    string? Cidr,
    string Status,
    string? RequestFailureReason,
    string? RequestFailureDetail,
    DateTimeOffset? RequestFailedAt,
    DateTimeOffset CreatedAt,
    string? PublicIpv6Subnet)
{
    public static AdminNetworkQueryResult From(NetworkAggregate n) =>
        new(n.Id, n.TenantId, n.Name.Value, n.ClusterId, n.Cidr, n.Status.ToString(),
            n.Operation.FailureReason, n.Operation.FailureDetail, n.Operation.FailedAt, n.CreatedAt, n.PublicIpv6Subnet);
}
