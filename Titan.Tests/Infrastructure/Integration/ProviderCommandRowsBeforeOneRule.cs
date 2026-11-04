using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A provider command row as a schema before the migration ProviderCommandOneRule stores it: with the NOT NULL rerun
/// column, which the model no longer maps, so a migration test that seeds before that migration writes the row in SQL.
internal static class ProviderCommandRowsBeforeOneRule
{
    public static Task InsertAsync(TitanDbContext db, string key, string kind, string clusterId, string payload,
        string status = "Pending", string rerun = "AfterAnyFinish")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? finishedAt = status is "Done" or "Failed" ? now : null;
        string? lastError = status == "Failed" ? "gave up" : null;
        return db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.provider_commands (id, key, kind, cluster_id, payload, rerun, status, attempts, available_at, last_error,
                                    created_at, finished_at, version)
VALUES ({Guid.NewGuid()}, {key}, {kind}, {clusterId}, CAST({payload} AS jsonb), {rerun}, {status}, 0, {now}, {lastError},
        {now}, {finishedAt}, 0)");
    }
}
