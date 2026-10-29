using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Volume;

public class DeleteVolumeCommandHandler(
    IVolumeRepository repo,
    IVolumeSnapshotRepository snapshotRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteVolumeCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volume = await ComputeOwnership.RequireVolumeAsync(repo, callerId, command.VolumeId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This delete is still waiting, or the volume is already deleted. A delete that failed is not a repeat: it
        // falls through and the aggregate decides. Answer like the first request: nothing recorded, nothing sent.
        bool deleteIsWaiting = volume.Transition == VolumeTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || volume.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // The create failed before Incus had it: nothing to delete on Incus
        // (titan-ops architecture/desired-and-current.md#delete-locally). The request and its outcome happen here at once.
        bool neverReachedIncus = volume.ExternalId is null && volume.Transition == null;
        if (neverReachedIncus)
        {
            volume.DeleteLocally();
            activity.Stage("volume.delete_requested", "volume", volume.Id, volume.Name); // atomic with DeleteLocally
            activity.StageOutcome(volume.TenantId, "volume.deleted", "volume", volume.Id, volume.Name);
            await repo.SaveAsync(ct);
            return;
        }

        int liveSnapshotCount = await snapshotRepo.CountActiveByVolumeIdAsync(volume.Id, ct);
        string externalVolumeId = volume.RequestDelete(liveSnapshotCount);

        activity.Stage("volume.delete_requested", "volume", volume.Id, volume.Name); // atomic with RequestDelete
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeDeleteAsync(volume.Id, externalVolumeId, projectName, volume.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
