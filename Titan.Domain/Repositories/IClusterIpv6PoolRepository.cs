using Titan.Domain.Aggregates.ClusterIpv6Pool;

namespace Titan.Domain.Repositories;

public interface IClusterIpv6PoolRepository
{
    /// The cluster's pool with its holdings, tracked; a new empty pool, tracked and not yet saved, when the cluster has
    /// none. The caller's own save writes it, so a network and the block it takes commit together
    /// (titan-ops features/network/design.md#ipv6-public).
    Task<ClusterIpv6Pool> FindOrAddAsync(string clusterId, CancellationToken ct = default);

    /// The cluster's pool with its holdings, tracked; null when no network of the cluster ever held a block.
    Task<ClusterIpv6Pool?> FindAsync(string clusterId, CancellationToken ct = default);
}
