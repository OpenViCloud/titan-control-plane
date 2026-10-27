using FastEndpoints;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Catalog.Cluster;
using Titan.Application.Queries.Catalog.Cluster.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

public class ListClustersEndpoint(ListClustersQueryHandler handler) : EndpointWithoutRequest<List<ClusterInfo>>
{
    public override void Configure()
    {
        Get("v1/clusters");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListClustersQuery(), ct), cancellation: ct);
    }
}
