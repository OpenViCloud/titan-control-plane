using Titan.Application.Services.Compute;
using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;

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

        // Locked: the per-volume cap is counted on the snapshots loaded with the volume, so a second request at the
        // cap waits here and then counts this one (titan-ops architecture/code-structure.md#child-entity).
        VolumeAggregate volume = await ComputeOwnership.RequireVolumeForUpdateAsync(volumeRepo, callerId, command.VolumeId, ct);

        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.VolumeSnapshot, volume.SnapshotQuotaUsage);

        // The volume checks itself (resting on Incus) and adds the snapshot in one call.
        Guid snapshotId = Guid.NewGuid();
        OperationTicket ticket = volume.AddSnapshot(snapshotId, command.Name);
        VolumeSnapshotEntity snapshot = ComputeOwnership.RequireVolumeSnapshot(volume, snapshotId);
        activity.Stage("volume_snapshot.create_requested", "volume_snapshot", snapshotId, command.Name); // atomic with the save
        await commands.EnqueueVolumeSnapshotCreateAsync(volume, snapshot, ticket.OperationId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(volume.TenantId), ct);
        await volumeRepo.SaveAsync(ct);

        return new CreateVolumeSnapshotCommandResult(snapshotId);
    }
}
