using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

public class DeleteVolumeSnapshotCommandHandler(
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

        // A snapshot of another volume answers like a missing one (titan-ops architecture/code-structure.md#child-entity).
        VolumeSnapshotEntity snapshot = volume.RequireSnapshot(command.SnapshotId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This delete is still waiting, or the snapshot is already deleted. A delete that failed is not a repeat:
        // it falls through and the aggregate decides. Answer like the first request: nothing recorded, nothing sent.
        bool deleteIsWaiting = snapshot.Transition == VolumeSnapshotTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || snapshot.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // The create failed before Incus had it: nothing to delete on Incus
        // (titan-ops architecture/desired-and-current.md#delete-locally). The request and its outcome happen here at once.
        bool neverReachedIncus = snapshot.ExternalId is null && snapshot.Transition == null;
        if (neverReachedIncus)
        {
            volume.DeleteSnapshotLocally(snapshot.Id);
            activity.Stage("volume_snapshot.delete_requested", "volume_snapshot", snapshot.Id, snapshot.Name); // atomic with DeleteLocally
            activity.StageOutcome(snapshot.TenantId, "volume_snapshot.deleted", "volume_snapshot", snapshot.Id, snapshot.Name);
            await volumeRepo.SaveAsync(ct);
            return;
        }

        string externalSnapshotId = volume.RequestSnapshotDelete(snapshot.Id);
        activity.Stage("volume_snapshot.delete_requested", "volume_snapshot", snapshot.Id, snapshot.Name); // atomic with RequestDelete

        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeSnapshotDeleteAsync(snapshot.Id, volumeId, externalSnapshotId, projectName,
            volume.ClusterId, ct);
        await volumeRepo.SaveAsync(ct);
    }
}
