using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class BackupObservationRepository(TitanDbContext db) : IBackupObservationRepository
{
    public Task<BackupObservation?> FindAsync(string kind, string resourceName, CancellationToken ct = default) =>
        db.BackupObservations.FirstOrDefaultAsync(o => o.Kind == kind && o.ResourceName == resourceName, ct);

    public void Add(BackupObservation observation) => db.BackupObservations.Add(observation);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
