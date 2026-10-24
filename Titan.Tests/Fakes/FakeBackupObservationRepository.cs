using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Repositories.Platform;

namespace Titan.Tests.Fakes;

public sealed class FakeBackupObservationRepository : IBackupObservationRepository
{
    public List<BackupObservation> Rows { get; } = new();

    public Task<BackupObservation?> FindAsync(string kind, string resourceName, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(o => o.Kind == kind && o.ResourceName == resourceName));

    public void Add(BackupObservation observation) => Rows.Add(observation);

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
