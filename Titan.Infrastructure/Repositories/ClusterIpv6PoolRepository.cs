using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class ClusterIpv6PoolRepository(TitanDbContext db) : IClusterIpv6PoolRepository
{
    public async Task<ClusterIpv6Pool> FindOrAddAsync(string clusterId, CancellationToken ct = default)
    {
        ClusterIpv6Pool? pool = await FindAsync(clusterId, ct);
        if (pool == null)
        {
            pool = ClusterIpv6Pool.Create(clusterId);
            db.ClusterIpv6Pools.Add(pool);
        }
        return pool;
    }

    public async Task<ClusterIpv6Pool?> FindAsync(string clusterId, CancellationToken ct = default) =>
        await db.ClusterIpv6Pools.Include(p => p.Holdings).FirstOrDefaultAsync(p => p.ClusterId == clusterId, ct);
}
