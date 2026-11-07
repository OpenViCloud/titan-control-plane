using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using Titan.Application.Exceptions;

namespace Titan.Application.Services.Compute;

/// <summary>
/// What every VM and VM snapshot callback handler does with a worker's report, in one place: parse the outcome and the
/// Incus status, record the outcome of a request that ended once (titan-ops features/activity/design.md#request-and-outcome-names), and release
/// the volumes of a VM whose delete was done, or keep them attached if it gave up
/// (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class VmReports
{
    // Keyed by the transition status that ended: it says what the operation was.
    private static readonly Dictionary<VmStatus, string> DoneAction = new()
    {
        [VmStatus.Creating] = "vm.created",
        [VmStatus.Starting] = "vm.started",
        [VmStatus.Stopping] = "vm.stopped",
        [VmStatus.Deleting] = "vm.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<VmStatus, string> FailedAction = new()
    {
        [VmStatus.Starting] = "vm.start_failed",
        [VmStatus.Stopping] = "vm.stop_failed",
        [VmStatus.Deleting] = "vm.delete_failed",
    };

    // Keyed by the status the snapshot waited in, which says the operation that ended.
    private static readonly Dictionary<VmSnapshotStatus, string> SnapshotDoneAction = new()
    {
        [VmSnapshotStatus.Creating] = "vm_snapshot.created",
        [VmSnapshotStatus.Reverting] = "vm_snapshot.reverted",
        [VmSnapshotStatus.Deleting] = "vm_snapshot.deleted",
    };

    private static readonly Dictionary<VmSnapshotStatus, string> SnapshotFailedAction = new()
    {
        [VmSnapshotStatus.Reverting] = "vm_snapshot.revert_failed",
        [VmSnapshotStatus.Deleting] = "vm_snapshot.delete_failed",
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

    // The worker sends the Incus status word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    // Matched by name, exactly: Enum.TryParse would also take "1" or "running".
    public static VmStatus ParseIncusStatus(string? status)
    {
        foreach (VmStatus known in Enum.GetValues<VmStatus>())
        {
            if (VmAggregate.IsIncusStatus(known) && known.ToString() == status)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown VM status: '{status}'");
    }

    /// The status of the instance the report saw, or null when it saw none. Handlers call it before loading the VM:
    /// a malformed report is the worker's bug, whatever the VM.
    public static VmStatus? ParseObservedStatus(VmObservationReport? report)
    {
        if (report == null)
        {
            return null;
        }
        return ParseIncusStatus(report.Status);
    }

    /// The addresses of the instance the report saw, or null when it saw none.
    public static string? ObservedIpAddress(VmObservationReport? report)
    {
        if (report == null)
        {
            return null;
        }
        return report.IpAddress;
    }

    public static string? ObservedPublicIpv6Address(VmObservationReport? report)
    {
        if (report == null)
        {
            return null;
        }
        return report.PublicIpv6Address;
    }

    /// Ends the VM's waiting operation with a Done and records its outcome once, with what a delete took along: the
    /// snapshots whose own delete was waiting, and the volumes the provider kept and detached
    /// (titan-ops features/volume/design.md#vm-delete-releases-volumes). Null when the report names no waiting
    /// operation: nothing is recorded.
    public static async Task<VmStatus?> ApplyDoneAsync(VmAggregate vm, Guid operationId,
        string? externalId, VmStatus? status, string? ipAddress, string? publicIpv6Address,
        IActivityRecorder activity, IVolumeRepository volumes, CancellationToken ct)
    {
        // The move the VM waits in says which Done this is.
        VmStatus waiting = vm.Status;
        // Read before the VM's delete Done takes every snapshot along: the ones waiting on their own delete get its
        // outcome here, once, since their command will only find them gone.
        List<VmSnapshot> snapshotsWaitingOnTheirDelete = new();
        foreach (VmSnapshot snapshot in vm.Snapshots)
        {
            if (snapshot.IsDeletedOrDeleting && !snapshot.IsDeleted)
            {
                snapshotsWaitingOnTheirDelete.Add(snapshot);
            }
        }

        bool ended;
        switch (waiting)
        {
            case VmStatus.Creating:
                ended = vm.CreateSucceeded(operationId, externalId, status, ipAddress, publicIpv6Address);
                break;
            case VmStatus.Starting:
                ended = vm.StartSucceeded(operationId, status, ipAddress, publicIpv6Address);
                break;
            case VmStatus.Stopping:
                ended = vm.StopSucceeded(operationId, status, ipAddress, publicIpv6Address);
                break;
            case VmStatus.Deleting:
                ended = vm.DeleteSucceeded(operationId);
                break;
            default:
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (DoneAction.TryGetValue(waiting, out string? action))
        {
            activity.StageOutcome(vm.TenantId, action, "vm", vm.Id, vm.Name);
        }
        if (waiting != VmStatus.Deleting)
        {
            return waiting;
        }
        foreach (VmSnapshot snapshot in snapshotsWaitingOnTheirDelete)
        {
            activity.StageOutcome(snapshot.TenantId, "vm_snapshot.deleted", "vm_snapshot", snapshot.Id, snapshot.Name);
        }
        // A volume's own waiting request is not touched: its command reports it.
        List<VolumeAggregate> attachedVolumes = await volumes.ListAttachedToVmLockedAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            volume.MarkDetachedByVmDelete(vm.Id);
            volume.ReleaseFromVmDelete(vm.Id);
        }
        return waiting;
    }

    /// Ends the VM's waiting operation with a give-up and records its outcome once; the volumes of a delete that gave
    /// up stay attached (titan-ops architecture/resource-lifecycle.md#kinds). Null when the report names no waiting
    /// operation: nothing is recorded.
    public static async Task<VmStatus?> ApplyFailedAsync(VmAggregate vm, Guid operationId,
        string? externalId, string? reason, string? detail,
        IActivityRecorder activity, IVolumeRepository volumes, CancellationToken ct)
    {
        // The move the VM waits in says which give-up this is. A worker sends no Incus status with a give-up.
        VmStatus waiting = vm.Status;
        bool ended;
        switch (waiting)
        {
            case VmStatus.Creating:
                ended = vm.CreateFailed(operationId, externalId, reason, detail);
                break;
            case VmStatus.Starting:
                ended = vm.StartFailed(operationId, reason, detail);
                break;
            case VmStatus.Stopping:
                ended = vm.StopFailed(operationId, reason, detail);
                break;
            case VmStatus.Deleting:
                ended = vm.DeleteFailed(operationId, reason, detail);
                break;
            default:
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (FailedAction.TryGetValue(waiting, out string? action))
        {
            activity.StageOutcome(vm.TenantId, action, "vm", vm.Id, vm.Name);
        }
        if (waiting != VmStatus.Deleting)
        {
            return waiting;
        }
        List<VolumeAggregate> attachedVolumes = await volumes.ListAttachedToVmLockedAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);
        }
        return waiting;
    }

    public static void RecordSnapshotEnded(VmSnapshot snapshot, VmSnapshotStatus ended,
        OperationOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<VmSnapshotStatus, string> actions =
            outcome == OperationOutcome.Done ? SnapshotDoneAction : SnapshotFailedAction;
        if (actions.TryGetValue(ended, out string? action))
        {
            activity.StageOutcome(snapshot.TenantId, action, "vm_snapshot", snapshot.Id, snapshot.Name);
        }
    }
}
