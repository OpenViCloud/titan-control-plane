using Titan.Domain.Aggregates.Volume;

namespace Titan.Domain.Repositories;

public interface IVolumeRepository
{
    /// The aggregate whole: the volume with its snapshots, tracked (titan-ops architecture/code-structure.md#child-entity).
    Task<Volume?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// The same, with the volume's row locked until this context's next save commits: every lifecycle operation and report
    /// of the volume and its snapshots takes it (a volume's create excepted: its row is new), so two of them take turns
    /// and the second sees the first (titan-ops architecture/resource-lifecycle.md#one-key-per-operation); the per-volume
    /// snapshot cap is counted on the loaded snapshots under it.
    Task<Volume?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default);
    Task<List<Volume>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// All tenants: what the disk meter bills (Volume.IsBilled) — a volume Incus holds, including while a delete waits,
    /// until Incus has deleted it (titan-ops features/volume/design.md#counting-and-billing), unless an admin restored
    /// it from a backup (titan-ops features/backup/design.md#restore-as-new-resource). Independent of any VM's state,
    /// by design.
    Task<List<Volume>> ListBillableAsync(CancellationToken ct = default);
    /// All tenants: every volume Incus holds as far as Titan last knew (Volume.IsOnIncus), billed or not: what a backup
    /// can export.
    Task<List<Volume>> ListOnIncusAsync(CancellationToken ct = default);
    /// Volumes in a cluster the storage reconciler should observe: they have a provider id and are not Deleted.
    Task<List<Volume>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    /// Of these ids, those of a volume that is not Deleted: the backups of the others may go
    /// (titan-ops features/backup/design.md#backups-of-gone-resources).
    Task<List<Guid>> ListIdsNotDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// The volumes of these ids in any status, Deleted included, untracked and without their snapshots: to name them.
    Task<List<Volume>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    void Add(Volume volume);
    /// Volumes a VM holds or was asked to hold (attached, attaching, detaching), not yet deleted; tracked, with their
    /// snapshots, and their rows locked, since the VM's delete changes them. The caller holds the VM's row lock first:
    /// VM, then volumes, the one order every handler that locks both takes.
    Task<List<Volume>> ListAttachedToVmLockedAsync(Guid vmId, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
