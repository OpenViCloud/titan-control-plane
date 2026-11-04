using Titan.Domain.Values;
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
        var volume = await ComputeOwnership.RequireVolumeForUpdateAsync(volumeRepo, callerId, volumeId, ct);

        // A snapshot of another volume answers like a missing one (titan-ops architecture/code-structure.md#child-entity).
        VolumeSnapshotEntity snapshot = ComputeOwnership.RequireVolumeSnapshot(volume, command.SnapshotId);

        // A repeat is answered like the first request: nothing recorded, nothing sent
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A delete that failed is not a
        // repeat: it falls through and the aggregate decides.
        if (snapshot.IsDeletedOrDeleting)
        {
            return;
        }

        // A snapshot that never reached Incus is deleted here and gets no ticket
        // (titan-ops architecture/desired-and-current.md#delete-locally): the request and its outcome at once.
        RequestTicket? ticket = volume.RequestSnapshotDelete(snapshot.Id);
        activity.Stage("volume_snapshot.delete_requested", "volume_snapshot", snapshot.Id, snapshot.Name); // atomic with RequestDelete
        if (ticket == null)
        {
            activity.StageOutcome(snapshot.TenantId, "volume_snapshot.deleted", "volume_snapshot", snapshot.Id, snapshot.Name);
            await volumeRepo.SaveAsync(ct);
            return;
        }

        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeSnapshotDeleteAsync(snapshot.Id, ticket.RequestId, volumeId, ticket.ProviderId, projectName,
            volume.ClusterId, ct);
        await volumeRepo.SaveAsync(ct);
    }
}
