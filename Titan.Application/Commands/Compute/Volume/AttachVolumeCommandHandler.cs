using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Volume;

public class AttachVolumeCommandHandler(
    IVolumeRepository repo,
    IVmRepository vmRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(AttachVolumeCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volume = await ComputeOwnership.RequireVolumeForUpdateAsync(repo, callerId, command.VolumeId, ct);

        var vm = await ComputeOwnership.RequireVmAsync(vmRepo, callerId, command.VmId, ct);

        // Volume state, VM state and cluster match are all checked inside Attach — one call, and the
        // transition cannot happen with any of them skipped (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).

        // The aggregate refuses a repeat, so it is recognised here first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): a repeat succeeds like the first
        // request and sends nothing.
        if (volume.IsAttachWaitingOrAttachedTo(vm.Id))
        {
            return;
        }

        VolumeAttachProviderIds providerIds = volume.Attach(vm);

        activity.Stage("volume.attach_requested", "volume", volume.Id, volume.Name); // atomic with Attach
        await commands.EnqueueVolumeAttachAsync(volume, providerIds.OperationId, providerIds.ExternalVolumeId,
            providerIds.ExternalVmId, TenantAggregate.ProjectNameFor(volume.TenantId), ct);
        await repo.SaveAsync(ct);
    }
}
