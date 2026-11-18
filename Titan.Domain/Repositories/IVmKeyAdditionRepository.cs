using Titan.Domain.Aggregates.Vm;

namespace Titan.Domain.Repositories;

/// Read side of the key addition table (titan-ops architecture/code-structure.md#child-entity): an addition is the
/// VM's child entity, written through IVmRepository. These reads are not bound to one aggregate.
public interface IVmKeyAdditionRepository
{
    /// The customer's list for one VM, newest first.
    Task<List<VmKeyAddition>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default);
    /// One addition by its own id, whatever VM it belongs to: the caller checks VmId against the route.
    Task<VmKeyAddition?> FindByIdAsync(Guid keyAdditionId, CancellationToken ct = default);
}
