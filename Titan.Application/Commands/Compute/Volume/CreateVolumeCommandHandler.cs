using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Volume;

public class CreateVolumeCommandHandler(
    IVolumeRepository repo,
    IVolumeOfferingRepository offeringRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ClusterOptions clusters)
{
    public async Task<CreateVolumeCommandResult> HandleAsync(CreateVolumeCommand req, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();

        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        // A volume without a price on file would stop the disk meter (titan-ops features/volume/design.md#offering-must-exist).
        var offering = await offeringRepo.FindByIdAsync(req.VolumeOfferingId, ct);
        if (offering == null)
        {
            throw new NotFoundException("VOLUME_OFFERING_NOT_FOUND",
                $"Volume offering '{req.VolumeOfferingId}' not found.");
        }

        var activeVolumeCount = await repo.CountActiveByTenantAsync(callerId, ct);

        // Quota refuses inside the factory — see CreateVmCommandHandler.
        var volume = VolumeAggregate.Create(tenant, activeVolumeCount, req.Name, req.ClusterId, req.VolumeOfferingId, req.SizeGb);
        clusters.ClusterOf(volume.ClusterId); // titan-ops features/catalog/design.md#cluster-is-a-real-choice
        repo.Add(volume);
        activity.Stage("volume.create_requested", "volume", volume.Id, volume.Name); // atomic with the volume insert
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeCreateAsync(
            volume.Id, volume.Name, volume.ClusterId, volume.SizeGb, projectName, ct);
        await repo.SaveAsync(ct);

        return new CreateVolumeCommandResult(volume.Id);
    }
}
