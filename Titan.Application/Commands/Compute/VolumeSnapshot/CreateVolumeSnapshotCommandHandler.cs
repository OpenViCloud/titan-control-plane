using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VolumeSnapshotAggregate = Titan.Domain.Aggregates.VolumeSnapshot.VolumeSnapshot;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

public class CreateVolumeSnapshotCommandHandler(
    IVolumeSnapshotRepository snapshotRepo,
    IVolumeRepository volumeRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateVolumeSnapshotCommandResult> HandleAsync(CreateVolumeSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volumeId = command.VolumeId;
        var volume = await ComputeOwnership.RequireVolumeAsync(volumeRepo, callerId, volumeId, ct);

        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
        var activeSnapshotCount = await snapshotRepo.CountActiveByVolumeIdAsync(volumeId, ct);

        var snapshot = VolumeSnapshotAggregate.Create(volume, tenant, activeSnapshotCount, command.Name); // volume state AND quota
        var externalVolumeId = volume.BeginSnapshot();                    // same rule, and hands over the id
        activity.Stage("volume_snapshot.create_requested", "volume_snapshot", snapshot.Id, snapshot.Name); // atomic with the insert
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeSnapshotCreateAsync(snapshot.Id, volumeId, externalVolumeId, projectName,
            volume.ClusterId, ct);
        await snapshotRepo.SaveAsync(snapshot, ct);

        return new CreateVolumeSnapshotCommandResult(snapshot.Id);
    }
}
