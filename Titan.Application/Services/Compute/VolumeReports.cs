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
/// (titan-ops features/activity/design.md#request-and-outcome-names). The new routes and the old ones share it, so they
/// cannot drift apart (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class VolumeReports
{
    private static readonly Dictionary<VolumeOperationKind, string> DoneAction = new()
    {
        [VolumeOperationKind.Create] = "volume.created",
        [VolumeOperationKind.Attach] = "volume.attached",
        [VolumeOperationKind.Detach] = "volume.detached",
        [VolumeOperationKind.Delete] = "volume.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<VolumeOperationKind, string> FailedAction = new()
    {
        [VolumeOperationKind.Attach] = "volume.attach_failed",
        [VolumeOperationKind.Detach] = "volume.detach_failed",
        [VolumeOperationKind.Delete] = "volume.delete_failed",
    };

    private static readonly Dictionary<VolumeSnapshotOperationKind, string> SnapshotDoneAction = new()
    {
        [VolumeSnapshotOperationKind.Create] = "volume_snapshot.created",
        [VolumeSnapshotOperationKind.Delete] = "volume_snapshot.deleted",
    };

    private static readonly Dictionary<VolumeSnapshotOperationKind, string> SnapshotFailedAction = new()
    {
        [VolumeSnapshotOperationKind.Delete] = "volume_snapshot.delete_failed",
    };

    public static OperationOutcome ParseOutcome(string outcome)
    {
        switch (outcome)
        {
            case "Done":
                return OperationOutcome.Done;
            case "Failed":
                return OperationOutcome.Failed;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown request outcome: '{outcome}'");
        }
    }

    // The worker reports instance names as Incus lists them; which of them are Titan VMs is Titan's to say
    // (titan-ops features/volume/design.md#attachment-from-used-by). Only the volume's own tenant is searched: an instance
    // name of another tenant's VM must never read as this volume's VM. A name matching no VM, or a second VM, is an
    // instance Titan does not manage for this volume.
    public static async Task<VolumeObservation> ResolveAttachmentAsync(
        IVmRepository vmRepo, Guid tenantId, List<string>? instanceNames, CancellationToken ct)
    {
        bool noInstance = instanceNames == null || instanceNames.Count == 0;
        if (noInstance)
        {
            return new VolumeObservation(null, false);
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
        return new VolumeObservation(attachedVmId, elsewhere);
    }

    /// Ends the volume's waiting operation with a Done and records its outcome once, with the snapshots a delete took
    /// along whose own delete was waiting. Null when the report names no waiting operation: nothing is recorded.
    public static PendingOperation<VolumeOperationKind>? FinishOperation(VolumeAggregate volume, Guid operationId,
        string? externalId, VolumeObservation? observed, IActivityRecorder activity)
    {
        // Read before the volume's delete Done takes every snapshot along: the ones waiting on their own delete get its
        // outcome here, once, since their command will only find them gone.
        List<VolumeSnapshot> snapshotsWaitingOnTheirDelete = new();
        foreach (VolumeSnapshot snapshot in volume.Snapshots)
        {
            if (snapshot.IsDeletedOrDeleting && !snapshot.IsTerminal)
            {
                snapshotsWaitingOnTheirDelete.Add(snapshot);
            }
        }

        PendingOperation<VolumeOperationKind>? ended = volume.FinishOperation(operationId, externalId, observed);
        if (ended == null)
        {
            return null;
        }
        if (DoneAction.TryGetValue(ended.Kind, out string? action))
        {
            activity.StageOutcome(volume.TenantId, action, "volume", volume.Id, volume.Name);
        }
        if (ended.Kind == VolumeOperationKind.Delete)
        {
            foreach (VolumeSnapshot snapshot in snapshotsWaitingOnTheirDelete)
            {
                activity.StageOutcome(snapshot.TenantId, "volume_snapshot.deleted", "volume_snapshot", snapshot.Id, snapshot.Name);
            }
        }
        return ended;
    }

    /// Ends the volume's waiting operation with a give-up and records its outcome once. Null when the report names no
    /// waiting operation: nothing is recorded.
    public static PendingOperation<VolumeOperationKind>? FailOperation(VolumeAggregate volume, Guid operationId,
        string? externalId, VolumeObservation? observed, string? reason, string? detail, IActivityRecorder activity)
    {
        PendingOperation<VolumeOperationKind>? ended = volume.FailOperation(operationId, externalId, observed, reason, detail);
        if (ended == null)
        {
            return null;
        }
        if (FailedAction.TryGetValue(ended.Kind, out string? action))
        {
            activity.StageOutcome(volume.TenantId, action, "volume", volume.Id, volume.Name);
        }
        return ended;
    }

    public static void RecordSnapshotEnded(VolumeSnapshot snapshot, PendingOperation<VolumeSnapshotOperationKind> ended,
        OperationOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<VolumeSnapshotOperationKind, string> actions =
            outcome == OperationOutcome.Done ? SnapshotDoneAction : SnapshotFailedAction;
        if (actions.TryGetValue(ended.Kind, out string? action))
        {
            activity.StageOutcome(snapshot.TenantId, action, "volume_snapshot", snapshot.Id, snapshot.Name);
        }
    }

    /// TEMPORARY, removed in rollout phase 6 (titan-ops architecture/resource-lifecycle.md#rollout): a worker from before
    /// the request routes reports an attach or detach only as an observation, so an observation that shows what a
    /// waiting attach or detach asked for ends it, as before this rollout. With the new worker the command's own Done
    /// and this race; the later one finds nothing waiting and is ignored, so the outcome is recorded once. A create or a
    /// delete is never ended here.
    public static PendingOperation<VolumeOperationKind>? EndAttachOrDetachTheObservationReached(VolumeAggregate volume,
        IActivityRecorder activity)
    {
        PendingOperation<VolumeOperationKind>? waiting = volume.Pending;
        if (waiting == null)
        {
            return null;
        }
        bool inUse = volume.CurrentAttachedVmId != null || volume.CurrentAttachedElsewhere;
        bool reached = (waiting.Kind == VolumeOperationKind.Attach && volume.CurrentAttachedVmId == volume.DesiredAttachedVmId)
                       || (waiting.Kind == VolumeOperationKind.Detach && !inUse);
        if (!reached)
        {
            return null;
        }
        return FinishOperation(volume, waiting.Id, null, null, activity);
    }
}
