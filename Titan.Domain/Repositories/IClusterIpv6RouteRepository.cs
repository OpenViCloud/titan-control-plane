using Titan.Domain.Aggregates.ClusterIpv6Route;

namespace Titan.Domain.Repositories;

public interface IClusterIpv6RouteRepository
{
    /// Every free route of the cluster, tracked and locked until the caller's save commits. A second caller waits here,
    /// then sees what the first left free, so two creates never take one route.
    Task<IReadOnlyList<ClusterIpv6Route>> LockFreeAsync(string clusterId, CancellationToken ct = default);

    /// The route the network holds, tracked; null when it holds none.
    Task<ClusterIpv6Route?> FindHeldByAsync(Guid networkId, CancellationToken ct = default);

    /// Adds the routes of the cluster that have no row yet, and leaves the others as they are. Commits by itself.
    Task EnsureRoutesAsync(string clusterId, CancellationToken ct = default);
}
