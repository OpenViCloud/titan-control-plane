using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Services.Compute;

/// <summary>
/// What every VM and VM snapshot callback handler does with a worker's report, in one place: parse the outcome and the
/// Incus status, record the outcome of a request that ended once (titan-ops features/activity/design.md#request-and-outcome-names), and release
/// the volumes of a VM whose delete was done, or keep them attached if it gave up. The new routes and the old ones share it, so they cannot drift apart
/// (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class VmReports
{
    private static readonly Dictionary<VmRequestKind, string> DoneAction = new()
    {
        [VmRequestKind.Create] = "vm.created",
        [VmRequestKind.Start] = "vm.started",
        [VmRequestKind.Stop] = "vm.stopped",
        [VmRequestKind.Delete] = "vm.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<VmRequestKind, string> FailedAction = new()
    {
        [VmRequestKind.Start] = "vm.start_failed",
        [VmRequestKind.Stop] = "vm.stop_failed",
        [VmRequestKind.Delete] = "vm.delete_failed",
    };

    // The tenant-facing reason when a give-up arrives on the old intents route, which carries none.
    private static readonly Dictionary<VmRequestKind, string> FixedFailureReason = new()
    {
        [VmRequestKind.Create] = "VM provisioning failed due to a system error. Please try again or contact support.",
        [VmRequestKind.Start] = "The VM could not be started. Please try again or contact support.",
        [VmRequestKind.Stop] = "The VM could not be stopped. Please try again or contact support.",
        [VmRequestKind.Delete] = "The VM could not be deleted. Please try again or contact support.",
    };

    private static readonly Dictionary<VmSnapshotRequestKind, string> SnapshotDoneAction = new()
    {
        [VmSnapshotRequestKind.Create] = "vm_snapshot.created",
        [VmSnapshotRequestKind.Revert] = "vm_snapshot.reverted",
        [VmSnapshotRequestKind.Delete] = "vm_snapshot.deleted",
    };

    private static readonly Dictionary<VmSnapshotRequestKind, string> SnapshotFailedAction = new()
    {
        [VmSnapshotRequestKind.Revert] = "vm_snapshot.revert_failed",
        [VmSnapshotRequestKind.Delete] = "vm_snapshot.delete_failed",
    };

    public static string ReasonFor(VmRequestKind kind) => FixedFailureReason[kind];

    public static RequestOutcome ParseOutcome(string outcome)
    {
        switch (outcome)
        {
            case "Done":
                return RequestOutcome.Done;
            case "Failed":
                return RequestOutcome.Failed;
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

    public static VmObservation? ParseObservation(VmObservationReport? report) =>
        report == null ? null : new VmObservation(ParseIncusStatus(report.Status), report.IpAddress, report.PublicIpv6Address);

    /// Records the ended request's outcome once, and what a delete took with it: the snapshots whose own delete was
    /// waiting, and the volumes the provider kept and detached
    /// (titan-ops features/volume/design.md#vm-delete-releases-volumes), which stay attached if the delete gave up
    /// (titan-ops architecture/resource-lifecycle.md#kinds).
    public static async Task RecordEndedAsync(
        VmAggregate vm, VmRequestEnded ended, RequestOutcome outcome,
        IActivityRecorder activity, IVolumeRepository volumes, CancellationToken ct)
    {
        VmRequestKind kind = ended.Request.Kind;
        Dictionary<VmRequestKind, string> actions = outcome == RequestOutcome.Done ? DoneAction : FailedAction;
        if (actions.TryGetValue(kind, out string? action))
        {
            activity.StageOutcome(vm.TenantId, action, "vm", vm.Id, vm.Name);
        }
        foreach (VmSnapshot snapshot in ended.SnapshotsWhoseDeleteEnded)
        {
            activity.StageOutcome(snapshot.TenantId, "vm_snapshot.deleted", "vm_snapshot", snapshot.Id, snapshot.Name);
        }

        if (kind != VmRequestKind.Delete)
        {
            return;
        }
        // A volume's own waiting request is not touched: its command reports it.
        List<VolumeAggregate> attachedVolumes = await volumes.ListAttachedToVmLockedAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            if (outcome == RequestOutcome.Done)
            {
                volume.MarkDetachedByVmDelete(vm.Id);
                volume.ReleaseFromVmDelete(vm.Id);
            }
            else
            {
                volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);
            }
        }
    }

    public static void RecordSnapshotEnded(VmSnapshot snapshot, PendingRequest<VmSnapshotRequestKind> ended,
        RequestOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<VmSnapshotRequestKind, string> actions =
            outcome == RequestOutcome.Done ? SnapshotDoneAction : SnapshotFailedAction;
        if (actions.TryGetValue(ended.Kind, out string? action))
        {
            activity.StageOutcome(snapshot.TenantId, action, "vm_snapshot", snapshot.Id, snapshot.Name);
        }
    }

    /// TEMPORARY, removed in rollout phase 6 (titan-ops architecture/resource-lifecycle.md#rollout): while a worker that
    /// reports nothing when a stop or start succeeds may still run, an observation that reaches what a waiting stop or
    /// start asked for ends it, as before this rollout. With the new worker the command's own Done and this race; the
    /// later one finds nothing waiting and is ignored, so the outcome is recorded once. A create is never ended here.
    public static VmRequestEnded? EndStopOrStartTheObservationReached(VmAggregate vm)
    {
        PendingRequest<VmRequestKind>? waiting = vm.Pending;
        if (waiting == null)
        {
            return null;
        }
        bool runs = vm.CurrentState == VmState.Running || vm.CurrentState == VmState.Ready;
        bool reached = (waiting.Kind == VmRequestKind.Stop && vm.CurrentState == VmState.Stopped)
                       || (waiting.Kind == VmRequestKind.Start && runs);
        if (!reached)
        {
            return null;
        }
        return vm.EndRequest(waiting.Id, VmRequestEnd.Done(null, null));
    }
}
