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
    private static readonly Dictionary<VmOperationKind, string> DoneAction = new()
    {
        [VmOperationKind.Create] = "vm.created",
        [VmOperationKind.Start] = "vm.started",
        [VmOperationKind.Stop] = "vm.stopped",
        [VmOperationKind.Delete] = "vm.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<VmOperationKind, string> FailedAction = new()
    {
        [VmOperationKind.Start] = "vm.start_failed",
        [VmOperationKind.Stop] = "vm.stop_failed",
        [VmOperationKind.Delete] = "vm.delete_failed",
    };

    private static readonly Dictionary<VmSnapshotOperationKind, string> SnapshotDoneAction = new()
    {
        [VmSnapshotOperationKind.Create] = "vm_snapshot.created",
        [VmSnapshotOperationKind.Revert] = "vm_snapshot.reverted",
        [VmSnapshotOperationKind.Delete] = "vm_snapshot.deleted",
    };

    private static readonly Dictionary<VmSnapshotOperationKind, string> SnapshotFailedAction = new()
    {
        [VmSnapshotOperationKind.Revert] = "vm_snapshot.revert_failed",
        [VmSnapshotOperationKind.Delete] = "vm_snapshot.delete_failed",
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
    public static VmState ParseIncusStatus(string? status)
    {
        foreach (VmState known in Enum.GetValues<VmState>())
        {
            if (known.ToString() == status)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown VM status: '{status}'");
    }

    /// The status of the instance the report saw, or null when it saw none. Handlers call it before loading the VM:
    /// a malformed report is the worker's bug, whatever the VM.
    public static VmState? ParseObservedStatus(VmObservationReport? report)
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
    public static async Task<VmOperationKind?> FinishOperationAsync(VmAggregate vm, Guid operationId,
        string? externalId, VmState? status, string? ipAddress, string? publicIpv6Address,
        IActivityRecorder activity, IVolumeRepository volumes, CancellationToken ct)
    {
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

        VmOperationKind? ended = vm.FinishOperation(operationId, externalId,
            status: status, ipAddress: ipAddress, publicIpv6Address: publicIpv6Address);
        if (ended == null)
        {
            return null;
        }
        if (DoneAction.TryGetValue(ended.Value, out string? action))
        {
            activity.StageOutcome(vm.TenantId, action, "vm", vm.Id, vm.Name);
        }
        if (ended != VmOperationKind.Delete)
        {
            return ended;
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
        return ended;
    }

    /// Ends the VM's waiting operation with a give-up and records its outcome once; the volumes of a delete that gave
    /// up stay attached (titan-ops architecture/resource-lifecycle.md#kinds). Null when the report names no waiting
    /// operation: nothing is recorded.
    public static async Task<VmOperationKind?> FailOperationAsync(VmAggregate vm, Guid operationId,
        string? externalId, VmState? status, string? ipAddress, string? publicIpv6Address, string? reason, string? detail,
        IActivityRecorder activity, IVolumeRepository volumes, CancellationToken ct)
    {
        VmOperationKind? ended = vm.FailOperation(operationId, externalId,
            status: status, ipAddress: ipAddress, publicIpv6Address: publicIpv6Address, reason: reason, detail: detail);
        if (ended == null)
        {
            return null;
        }
        if (FailedAction.TryGetValue(ended.Value, out string? action))
        {
            activity.StageOutcome(vm.TenantId, action, "vm", vm.Id, vm.Name);
        }
        if (ended != VmOperationKind.Delete)
        {
            return ended;
        }
        List<VolumeAggregate> attachedVolumes = await volumes.ListAttachedToVmLockedAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);
        }
        return ended;
    }

    public static void RecordSnapshotEnded(VmSnapshot snapshot, VmSnapshotOperationKind ended,
        OperationOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<VmSnapshotOperationKind, string> actions =
            outcome == OperationOutcome.Done ? SnapshotDoneAction : SnapshotFailedAction;
        if (actions.TryGetValue(ended, out string? action))
        {
            activity.StageOutcome(snapshot.TenantId, action, "vm_snapshot", snapshot.Id, snapshot.Name);
        }
    }
}
