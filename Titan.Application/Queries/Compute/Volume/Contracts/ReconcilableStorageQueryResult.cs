using VolumeEntity = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;
using VolumeSnapshotStatus = Titan.Domain.Aggregates.Volume.VolumeSnapshotStatus;
using VolumeStatus = Titan.Domain.Aggregates.Volume.VolumeStatus;

namespace Titan.Application.Queries.Compute.Volume.Contracts;

// What the storage reconciler needs to match what Incus holds to Titan volumes and snapshots, and to report only what
// changed: CurrentExistence is whether Titan last knew Incus to hold it, worked out from the status and what a waiting
// operation last saw (the worker's contract kept its existence word, titan-ops features/volume/design.md#volume-routes).
// Internal only — never on a customer path.
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
    bool CurrentAttachedElsewhere)
{
    /// Exists when Incus was last seen holding the volume; NotFound otherwise, since every volume listed here has a
    /// provider id. A volume being deleted that Incus already lost stays NotFound, so the worker reports nothing new.
    public static string ExistenceName(VolumeEntity v)
    {
        if (v.InstanceStatus == VolumeStatus.Available)
        {
            return "Exists";
        }
        return "NotFound";
    }
}

// VolumeId is the parent volume: the snapshot's callback route needs it.
public record ReconcilableVolumeSnapshotQueryResult(Guid Id, Guid VolumeId, string ExternalId, string CurrentExistence)
{
    public static ReconcilableVolumeSnapshotQueryResult From(VolumeSnapshotEntity s) =>
        new(s.Id, s.VolumeId, s.GetExternalId(), ExistenceName(s));

    /// Exists when Incus was last seen holding the snapshot; NotFound otherwise, since every snapshot listed here has a
    /// provider id.
    public static string ExistenceName(VolumeSnapshotEntity s)
    {
        if (s.InstanceStatus == VolumeSnapshotStatus.Available)
        {
            return "Exists";
        }
        return "NotFound";
    }
}
