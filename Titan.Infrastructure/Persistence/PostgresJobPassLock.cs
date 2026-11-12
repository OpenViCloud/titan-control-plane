using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;

namespace Titan.Infrastructure.Persistence;

/// A transaction-level advisory lock of Postgres on the job's name, in the scope's DbContext: the commit of its next
/// SaveChangesAsync releases it (TitanDbContext.BeginLockTransactionAsync), and so does a scope that ends without one.
public sealed class PostgresJobPassLock(TitanDbContext db) : IJobPassLock
{
    public async Task TakeAsync(string job, CancellationToken ct)
    {
        await db.BeginLockTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({job}))", ct);
    }
}
