using FastEndpoints;
using Titan.Application.Abstractions;

namespace Titan.Infrastructure.Endpoints.Admin;

// Nothing secret in a cluster (titan-ops features/cluster/design.md#cluster-incus).
// Covered by AdminClusterServiceTests.
public class ListAdminClustersEndpoint(IAdminClusterService clusters)
    : EndpointWithoutRequest<List<AdminClusterDto>>
{
    public override void Configure()
    {
        Get("v1/admin/clusters");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await clusters.ListForAdminAsync(ct), cancellation: ct);
    }
}
