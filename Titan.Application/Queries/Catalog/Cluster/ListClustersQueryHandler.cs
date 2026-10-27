using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Catalog.Cluster.Contracts;

namespace Titan.Application.Queries.Catalog.Cluster;

public class ListClustersQueryHandler(ClusterOptions clusterOptions)
{
    public Task<List<ClusterInfo>> HandleAsync(ListClustersQuery query, CancellationToken ct = default)
    {
        var clusters = clusterOptions.Clusters
            .Select(z => new ClusterInfo(z.Id, z.Name))
            .ToList();
        return Task.FromResult(clusters);
    }
}
