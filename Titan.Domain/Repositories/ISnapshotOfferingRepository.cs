using Titan.Domain.Aggregates.SnapshotOffering;

namespace Titan.Domain.Repositories;

public interface ISnapshotOfferingRepository
{
    Task<SnapshotOffering?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<List<SnapshotOffering>> ListAllAsync(CancellationToken ct = default);
}
