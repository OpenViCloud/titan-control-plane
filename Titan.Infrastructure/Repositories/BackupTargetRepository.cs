using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Persistence;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Infrastructure.Repositories;

public class BackupTargetRepository(TitanDbContext db) : IBackupTargetRepository
{
    public Task<BackupTargetAggregate?> FindAsync(CancellationToken ct = default) =>
        db.BackupTargets.FirstOrDefaultAsync(t => t.Id == BackupTargetAggregate.SingletonId, ct);

    public void Add(BackupTargetAggregate target) => db.BackupTargets.Add(target);

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two admins saved the first target at the same time (same fixed id): the second one is told, nothing
            // was written (the audit row shares this save), and sending the form again replaces the first one's values.
            throw new ConcurrencyConflictException("BACKUP_TARGET_CONFLICT",
                "Another admin set the backup target at the same moment. Send it again.", ex);
        }
    }
}
