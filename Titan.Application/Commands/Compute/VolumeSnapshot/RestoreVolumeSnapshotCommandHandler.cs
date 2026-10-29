using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

public class RestoreVolumeSnapshotCommandHandler(
    IVolumeSnapshotRepository snapshotRepo,
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

        // Load source volume (may be Deleted — still allowed for restore). Deliberately NOT
        // ComputeOwnership.RequireVolumeAsync: that one is for volumes the caller is acting ON, and
        // this path must keep working after the source volume is gone.
        var sourceVolume = await volumeRepo.FindByIdAsync(volumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        if (sourceVolume.TenantId != callerId)
            throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        var snapshot = await ComputeOwnership.RequireVolumeSnapshotAsync(snapshotRepo, command.SnapshotId, volumeId, ct);

        // Restoring a snapshot creates a NEW volume, so it spends volume quota, not snapshot quota.
        // The factory decides; the handler only counts.
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
        var activeVolumeCount = await volumeRepo.CountActiveByTenantAsync(callerId, ct);

        var newVolume = VolumeAggregate.CreateFromSnapshot(
            snapshot, tenant, activeVolumeCount, command.Name, sourceVolume.ClusterId,
            sourceVolume.VolumeOfferingId, sourceVolume.SizeGb);
        var externalSnapshotId = snapshot.BeginRestore(); // same rule, and hands over the id
        volumeRepo.Add(newVolume);
        // The restore's result is a new volume, so the request is recorded against it; its outcome is
        // the "volume.created" the volume callback records on Allocating → Allocated (titan-ops features/activity/design.md#outcome-source).
        activity.Stage("volume_snapshot.restore_requested", "volume", newVolume.Id, newVolume.Name); // atomic with Add
        string projectName = TenantAggregate.ProjectNameFor(newVolume.TenantId);
        await commands.EnqueueVolumeSnapshotRestoreAsync(
            snapshot.Id, newVolume.Id, externalSnapshotId, command.Name, projectName, sourceVolume.ClusterId, ct);
        await volumeRepo.SaveAsync(ct);

        return new RestoreVolumeSnapshotCommandResult(newVolume.Id);
    }
}
