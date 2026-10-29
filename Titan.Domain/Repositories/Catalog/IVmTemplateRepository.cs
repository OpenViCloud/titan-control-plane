using Titan.Domain.Aggregates.Catalog.VmTemplate;

namespace Titan.Domain.Repositories.Catalog;

// Every read loads the cluster rows: a template's state lives in them (titan-ops features/catalog/design.md#template-per-cluster).
public interface IVmTemplateRepository
{
    Task<VmTemplate?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// What customers in a cluster may pick: templates that cluster's provider was last observed to hold.
    Task<List<VmTemplate>> ListSyncedInClusterAsync(string clusterId, CancellationToken ct = default);
    /// Templates synced in at least one cluster — what a VM anywhere can have been created from.
    Task<List<VmTemplate>> ListSyncedInAnyClusterAsync(CancellationToken ct = default);
    /// Templates with a provider id in the cluster — what that cluster's template reconciler can observe.
    Task<List<VmTemplate>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default);
    Task<List<VmTemplate>> ListAllAsync(CancellationToken ct = default);
    void Add(VmTemplate template);
    Task SaveAsync(CancellationToken ct = default);
}
