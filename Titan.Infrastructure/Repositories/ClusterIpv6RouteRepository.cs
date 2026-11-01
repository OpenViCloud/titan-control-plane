using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class ClusterIpv6RouteRepository(TitanDbContext db) : IClusterIpv6RouteRepository
{
    public async Task<IReadOnlyList<ClusterIpv6Route>> LockFreeAsync(string clusterId, CancellationToken ct = default)
    {
        await db.BeginLockTransactionAsync(ct);
        // One statement locks and reads: a later read could see a route freed since, which this create holds no lock on,
        // and two creates could then take it. By number, so two creates take the rows in one order and cannot deadlock.
        return await db.ClusterIpv6Routes
            .FromSql($"SELECT * FROM core.cluster_ipv6_routes WHERE cluster_id = {clusterId} AND network_id IS NULL ORDER BY number FOR UPDATE")
            .ToListAsync(ct);
    }

    public async Task<ClusterIpv6Route?> FindHeldByAsync(Guid networkId, CancellationToken ct = default) =>
        await db.ClusterIpv6Routes.FirstOrDefaultAsync(r => r.NetworkId == networkId, ct);

    public async Task EnsureRoutesAsync(string clusterId, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO core.cluster_ipv6_routes (cluster_id, number)
             SELECT {clusterId}, n FROM generate_series({ClusterIpv6Route.FirstNumber}, {ClusterIpv6Route.LastNumber}) n
             ON CONFLICT DO NOTHING
             """,
            ct);
}
