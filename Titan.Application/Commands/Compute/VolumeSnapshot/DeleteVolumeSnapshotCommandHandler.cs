using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

public class DeleteVolumeSnapshotCommandHandler(
    IVolumeSnapshotRepository snapshotRepo,
    IVolumeRepository volumeRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteVolumeSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volumeId = command.VolumeId;
        var volume = await ComputeOwnership.RequireVolumeAsync(volumeRepo, callerId, volumeId, ct);

        var snapshot = await ComputeOwnership.RequireVolumeSnapshotAsync(snapshotRepo, command.SnapshotId, volumeId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This delete is still waiting, or the snapshot is already deleted. A delete that failed is not a repeat:
        // it falls through and the aggregate decides. Answer like the first request: nothing recorded, nothing sent.
        bool deleteIsWaiting = snapshot.Transition == VolumeSnapshotTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || snapshot.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // The create failed before Incus had it: nothing to delete on Incus
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#delete-locally). The request and its outcome happen here at once.
        bool neverReachedIncus = snapshot.ExternalId is null && snapshot.Transition == null;
        if (neverReachedIncus)
        {
            snapshot.DeleteLocally();
            activity.Stage("volume_snapshot.delete_requested", "volume_snapshot", snapshot.Id, snapshot.Name); // atomic with DeleteLocally
            activity.StageOutcome(snapshot.TenantId, "volume_snapshot.deleted", "volume_snapshot", snapshot.Id, snapshot.Name);
            await snapshotRepo.SaveAsync(snapshot, ct);
            return;
        }

        string externalSnapshotId = snapshot.RequestDelete();
        activity.Stage("volume_snapshot.delete_requested", "volume_snapshot", snapshot.Id, snapshot.Name); // atomic with RequestDelete

        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeSnapshotDeleteAsync(snapshot.Id, volumeId, externalSnapshotId, projectName,
            volume.ClusterId, ct);
        await snapshotRepo.SaveAsync(snapshot, ct);
    }
}
