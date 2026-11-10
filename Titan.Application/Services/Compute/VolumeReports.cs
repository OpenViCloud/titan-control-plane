using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using Titan.Application.Exceptions;

namespace Titan.Application.Services.Compute;

/// <summary>
/// What every volume and volume snapshot callback handler does with a worker's report, in one place: parse the outcome,
/// resolve the instances using a volume to a Titan VM, and record the outcome of a request that ended once
/// (titan-ops features/activity/design.md#request-and-outcome-names).
/// </summary>
public static class VolumeReports
{
    // Keyed by the status the volume waited in, which says the operation that ended.
    private static readonly Dictionary<VolumeStatus, string> DoneAction = new()
    {
        [VolumeStatus.Creating] = "volume.created",
        [VolumeStatus.Attaching] = "volume.attached",
        [VolumeStatus.Detaching] = "volume.detached",
        [VolumeStatus.Deleting] = "volume.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<VolumeStatus, string> FailedAction = new()
    {
        [VolumeStatus.Attaching] = "volume.attach_failed",
        [VolumeStatus.Detaching] = "volume.detach_failed",
        [VolumeStatus.Deleting] = "volume.delete_failed",
    };

    private static readonly Dictionary<VolumeSnapshotStatus, string> SnapshotDoneAction = new()
    {
        [VolumeSnapshotStatus.Creating] = "volume_snapshot.created",
        [VolumeSnapshotStatus.Deleting] = "volume_snapshot.deleted",
    };

    private static readonly Dictionary<VolumeSnapshotStatus, string> SnapshotFailedAction = new()
    {
        [VolumeSnapshotStatus.Deleting] = "volume_snapshot.delete_failed",
    };


    // The worker reports instance names as Incus lists them; which of them are Titan VMs is Titan's to say
    // (titan-ops features/volume/design.md#attachment-from-used-by). Only the volume's own tenant is searched: an instance
    // name of another tenant's VM must never read as this volume's VM. A name matching no VM, or a second VM, is an
    // instance Titan does not manage for this volume.
    public static async Task<(Guid? AttachedVmId, bool AttachedElsewhere)> ResolveAttachmentAsync(
        IVmRepository vmRepo, Guid tenantId, List<string>? instanceNames, CancellationToken ct)
    {
        bool noInstance = instanceNames == null || instanceNames.Count == 0;
        if (noInstance)
        {
            return (null, false);
        }
        List<VmAggregate> vms = await vmRepo.ListByExternalIdsAsync(tenantId, instanceNames!, ct);
        Guid? attachedVmId = null;
        bool elsewhere = false;
        foreach (string instanceName in instanceNames!)
        {
            VmAggregate? vm = vms.FirstOrDefault(v => v.ExternalId == instanceName);
            bool isTheFirstTitanVm = vm != null && attachedVmId == null;
            if (isTheFirstTitanVm)
            {
                attachedVmId = vm!.Id;
            }
            else
            {
                elsewhere = true;
            }
        }
        return (attachedVmId, elsewhere);
    }

    /// Ends the volume's waiting operation with a Done and records its outcome once, with the snapshots a delete took
    /// along whose own delete was waiting. The status the volume waited in says which method ends it. Null when the report
    /// names no waiting operation: nothing is recorded; otherwise the status it waited in.
    public static VolumeStatus? FinishOperation(VolumeAggregate volume, Guid operationId,
        string? externalId, bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, IActivityRecorder activity)
    {
        // Read before the volume's delete Done takes every snapshot along: the ones waiting on their own delete get its
        // outcome here, once, since their command will only find them gone.
        List<VolumeSnapshot> snapshotsWaitingOnTheirDelete = new();
        foreach (VolumeSnapshot snapshot in volume.Snapshots)
        {
            if (snapshot.Status == VolumeSnapshotStatus.Deleting)
            {
                snapshotsWaitingOnTheirDelete.Add(snapshot);
            }
        }

        VolumeStatus waiting = volume.Status;
        bool ended;
        switch (waiting)
        {
            case VolumeStatus.Creating:
                ended = volume.CreateSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
                break;
            case VolumeStatus.Attaching:
                ended = volume.AttachSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
                break;
            case VolumeStatus.Detaching:
                ended = volume.DetachSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
                break;
            case VolumeStatus.Deleting:
                ended = volume.DeleteSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
                break;
            default:
                // Nothing waits: the report is stale.
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (DoneAction.TryGetValue(waiting, out string? action))
        {
            activity.StageOutcome(volume.TenantId, action, "volume", volume.Id, volume.Name);
        }
        if (waiting == VolumeStatus.Deleting)
        {
            foreach (VolumeSnapshot snapshot in snapshotsWaitingOnTheirDelete)
            {
                activity.StageOutcome(snapshot.TenantId, "volume_snapshot.deleted", "volume_snapshot", snapshot.Id, snapshot.Name);
            }
        }
        return waiting;
    }

    /// Ends the volume's waiting operation with a give-up and records its outcome once. Null when the report names no
    /// waiting operation: nothing is recorded; otherwise the status it waited in.
    public static VolumeStatus? FailOperation(VolumeAggregate volume, Guid operationId,
        string? externalId, bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, string? reason, string? detail,
        IActivityRecorder activity)
    {
        VolumeStatus waiting = volume.Status;
        bool ended;
        switch (waiting)
        {
            case VolumeStatus.Creating:
                ended = volume.CreateFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere,
                    reason, detail);
                break;
            case VolumeStatus.Attaching:
                ended = volume.AttachFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere,
                    reason, detail);
                break;
            case VolumeStatus.Detaching:
                ended = volume.DetachFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere,
                    reason, detail);
                break;
            case VolumeStatus.Deleting:
                ended = volume.DeleteFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere,
                    reason, detail);
                break;
            default:
                // Nothing waits: the report is stale.
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (FailedAction.TryGetValue(waiting, out string? action))
        {
            activity.StageOutcome(volume.TenantId, action, "volume", volume.Id, volume.Name);
        }
        return waiting;
    }

    public static void RecordSnapshotEnded(VolumeSnapshot snapshot, VolumeSnapshotStatus ended,
        OperationOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<VolumeSnapshotStatus, string> actions =
            outcome == OperationOutcome.Done ? SnapshotDoneAction : SnapshotFailedAction;
        if (actions.TryGetValue(ended, out string? action))
        {
            activity.StageOutcome(snapshot.TenantId, action, "volume_snapshot", snapshot.Id, snapshot.Name);
        }
    }
}
