using System.Data.Common;
using Npgsql;

namespace Titan.Infrastructure.Persistence;

/// Up to four control plane replicas share Postgres' max_connections: each one's pool is capped unless the connection
/// string sets its own size (titan-ops architecture/platform-infra.md#control-plane-replicas).
public static class TitanConnectionString
{
    public const int MaxPoolSize = 40;

    private static readonly HashSet<string> PoolSizeKeywords = ["maximumpoolsize", "maxpoolsize"];

    public static string WithPoolCap(string connectionString)
    {
        var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
        bool setsItsOwn = raw.Keys.Cast<string>().Any(k => PoolSizeKeywords.Contains(k.Replace(" ", "").ToLowerInvariant()));
        if (setsItsOwn)
        {
            return connectionString;
        }
        return new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = MaxPoolSize }.ConnectionString;
    }
}
