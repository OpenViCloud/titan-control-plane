using Titan.Application.Services.Compute;
using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

public class CreateVolumeSnapshotCommandHandler(
    IVolumeRepository volumeRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateVolumeSnapshotCommandResult> HandleAsync(CreateVolumeSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        ComputeInput.EnsureVolumeSnapshotName(command.Name);
        var volumeId = command.VolumeId;

        // Locked: the per-volume cap is counted on the snapshots loaded with the volume, so a second request at the
        // cap waits here and then counts this one (titan-ops architecture/code-structure.md#child-entity). A foreign
        // volume answers 404 like a missing one (titan-ops architecture/http-contract.md#tenant-scoping).
        VolumeAggregate volume = await volumeRepo.FindByIdForUpdateAsync(volumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");
        if (volume.TenantId != callerId)
            throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.VolumeSnapshot, volume.SnapshotQuotaUsage);

        // The volume checks itself (resting on Incus) and adds the snapshot in one call.
        Guid snapshotId = Guid.NewGuid();
        OperationTicket ticket = volume.AddSnapshot(snapshotId, command.Name);
        activity.Stage("volume_snapshot.create_requested", "volume_snapshot", snapshotId, command.Name); // atomic with the save
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeSnapshotCreateAsync(snapshotId, ticket.OperationId, volumeId, ticket.ProviderId, projectName,
            volume.ClusterId, ct);
        await volumeRepo.SaveAsync(ct);

        return new CreateVolumeSnapshotCommandResult(snapshotId);
    }
}
