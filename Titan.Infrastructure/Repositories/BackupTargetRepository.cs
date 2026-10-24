using Microsoft.EntityFrameworkCore;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Persistence;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Infrastructure.Repositories;

public class BackupTargetRepository(TitanDbContext db) : IBackupTargetRepository
{
    public Task<BackupTargetAggregate?> FindAsync(CancellationToken ct = default) =>
        db.BackupTargets.FirstOrDefaultAsync(t => t.Id == BackupTargetAggregate.SingletonId, ct);

    public void Add(BackupTargetAggregate target) => db.BackupTargets.Add(target);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
