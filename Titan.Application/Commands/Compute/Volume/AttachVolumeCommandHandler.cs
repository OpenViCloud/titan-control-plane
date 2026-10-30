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
        var volume = await ComputeOwnership.RequireVolumeAsync(repo, callerId, command.VolumeId, ct);

        var vm = await vmRepo.FindByIdAsync(command.VmId, ct);
        if (vm is null || vm.TenantId != callerId)
            throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        // Volume state, VM state and cluster match are all checked inside Attach — one call, and the
        // transition cannot happen with any of them skipped (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).

        // The aggregate refuses a repeat, so it is recognised here first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): this volume is already attaching
        // or attached to THIS VM. A repeat succeeds like the first request and sends nothing. Another VM falls
        // through and is refused.
        bool attachIsWaiting = volume.Transition == VolumeTransition.Attaching;
        bool restsAttached = volume.Transition == null && volume.CurrentAttachedVmId == volume.DesiredAttachedVmId;
        bool attachingOrAttached = attachIsWaiting || restsAttached;
        bool toThisVm = volume.DesiredAttachedVmId == vm.Id;
        if (attachingOrAttached && toThisVm)
        {
            return;
        }

        VolumeAttachProviderIds providerIds = volume.Attach(vm);

        activity.Stage("volume.attach_requested", "volume", volume.Id, volume.Name); // atomic with Attach
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeAttachAsync(
            volume.Id, providerIds.ExternalVolumeId, providerIds.ExternalVmId, projectName, volume.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
