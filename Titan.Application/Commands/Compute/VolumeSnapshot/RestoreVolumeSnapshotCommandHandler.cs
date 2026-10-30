using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

public class RestoreVolumeSnapshotCommandHandler(
    IVolumeRepository volumeRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<RestoreVolumeSnapshotCommandResult> HandleAsync(RestoreVolumeSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volumeId = command.VolumeId;

        // The source volume, whatever Incus holds of it: only the snapshot must rest on Incus
        // (titan-ops features/volume/design.md#volume-restore-is-a-new-volume). Deliberately NOT
        // ComputeOwnership.RequireVolumeAsync: that one is for volumes the caller is acting ON.
        var sourceVolume = await volumeRepo.FindByIdAsync(volumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        if (sourceVolume.TenantId != callerId)
            throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        // A snapshot of another volume answers like a missing one (titan-ops architecture/code-structure.md#child-entity).
        VolumeSnapshotEntity snapshot = sourceVolume.RequireSnapshot(command.SnapshotId);

        // Restoring a snapshot creates a NEW volume, so it spends volume quota, not snapshot quota.
        // The factory decides; the handler only counts.
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
        var activeVolumeCount = await volumeRepo.CountActiveByTenantAsync(callerId, ct);

        var newVolume = VolumeAggregate.CreateFromSnapshot(
            snapshot, tenant, activeVolumeCount, command.Name, sourceVolume.ClusterId,
            sourceVolume.VolumeOfferingId, sourceVolume.SizeGb);
        var externalSnapshotId = sourceVolume.BeginSnapshotRestore(snapshot.Id); // same rule, and hands over the id
        volumeRepo.Add(newVolume);
        // The restore's result is a new volume, so the request is recorded against it; its outcome is
        // the "volume.created" the volume callback records (titan-ops features/activity/design.md#outcome-source).
        activity.Stage("volume_snapshot.restore_requested", "volume", newVolume.Id, newVolume.Name); // atomic with Add
        string projectName = TenantAggregate.ProjectNameFor(newVolume.TenantId);
        await commands.EnqueueVolumeSnapshotRestoreAsync(
            snapshot.Id, newVolume.Id, externalSnapshotId, command.Name, projectName, sourceVolume.ClusterId, ct);
        await volumeRepo.SaveAsync(ct);

        return new RestoreVolumeSnapshotCommandResult(newVolume.Id);
    }
}
