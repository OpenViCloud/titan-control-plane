using VolumeSnapshotAggregate = Titan.Domain.Aggregates.Compute.VolumeSnapshot.VolumeSnapshot;

namespace Titan.Application.Queries.Compute.Volume.Contracts;

// What the storage reconciler needs to match what Incus holds to Titan volumes and snapshots, and to report only what
// changed: CurrentExistence is what Titan last recorded. Internal only — never on a customer path.
public record ReconcilableStorageQueryResult(
    List<ReconcilableVolumeQueryResult> Volumes,
    List<ReconcilableVolumeSnapshotQueryResult> Snapshots);

// CurrentAttachedExternalVmId is the Incus instance name of the Titan VM last seen using the volume, so the worker
// compares names it reads in used_by without knowing which instances are Titan VMs
// (titan-ops features/volume/design.md#attachment-from-used-by).
public record ReconcilableVolumeQueryResult(
    Guid Id,
    string ExternalId,
    string CurrentExistence,
    string? CurrentAttachedExternalVmId,
    bool CurrentAttachedElsewhere);

// VolumeId is the parent volume: the snapshot's callback route needs it.
public record ReconcilableVolumeSnapshotQueryResult(Guid Id, Guid VolumeId, string ExternalId, string CurrentExistence)
{
    public static ReconcilableVolumeSnapshotQueryResult From(VolumeSnapshotAggregate s) =>
        new(s.Id, s.VolumeId, s.RequireExternalId(), s.CurrentExistence.ToString());
}
