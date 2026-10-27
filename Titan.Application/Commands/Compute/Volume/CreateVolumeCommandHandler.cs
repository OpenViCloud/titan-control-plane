using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Volume;

public class CreateVolumeCommandHandler(
    IVolumeRepository repo,
    IVolumeOfferingRepository offeringRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ZoneOptions zones)
{
    public async Task<CreateVolumeCommandResult> HandleAsync(CreateVolumeCommand req, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();

        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        // A volume without a price on file would stop the disk meter (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#offering-must-exist).
        var offering = await offeringRepo.FindByIdAsync(req.VolumeOfferingId, ct);
        if (offering == null)
        {
            throw new NotFoundException("VOLUME_OFFERING_NOT_FOUND",
                $"Volume offering '{req.VolumeOfferingId}' not found.");
        }

        var activeVolumeCount = await repo.CountActiveByTenantAsync(callerId, ct);

        // Quota refuses inside the factory — see CreateVmCommandHandler.
        var volume = VolumeAggregate.Create(tenant, activeVolumeCount, req.Name, req.ZoneId, req.VolumeOfferingId, req.SizeGb);
        zones.ClusterOf(volume.ZoneId); // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#zone-is-a-real-choice
        repo.Add(volume);
        activity.Stage("volume.create_requested", "volume", volume.Id, volume.Name); // atomic with the volume insert
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeCreateAsync(
            volume.Id, volume.Name, volume.ZoneId, volume.SizeGb, projectName, ct);
        await repo.SaveAsync(ct);

        return new CreateVolumeCommandResult(volume.Id);
    }
}
