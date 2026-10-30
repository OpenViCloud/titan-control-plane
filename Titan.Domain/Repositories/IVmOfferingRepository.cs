using Titan.Domain.Aggregates.VmOffering;

namespace Titan.Domain.Repositories;

public interface IVmOfferingRepository
{
    Task<VmOffering?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<List<VmOffering>> ListAllAsync(CancellationToken ct = default);

    /// Shares the DbContext with the other repositories, so an audit row staged alongside a price change
    /// (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering) is written in the same transaction.
    Task SaveAsync(CancellationToken ct = default);
}
