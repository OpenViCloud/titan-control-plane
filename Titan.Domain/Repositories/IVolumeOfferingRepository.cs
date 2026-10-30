using Titan.Domain.Aggregates.VolumeOffering;

namespace Titan.Domain.Repositories;

public interface IVolumeOfferingRepository
{
    Task<VolumeOffering?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<List<VolumeOffering>> ListAllAsync(CancellationToken ct = default);
}
