using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Volume;

public class DetachVolumeCommandHandler(
    IVolumeRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DetachVolumeCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volume = await ComputeOwnership.RequireVolumeAsync(repo, callerId, command.VolumeId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // A detach is already running, or no instance uses the volume and none is asked for. One used outside Titan
        // is not a repeat: the aggregate refuses it.
        // Answer like the first request: nothing recorded, nothing sent.
        bool detachIsWaiting = volume.Transition == VolumeTransition.Detaching;
        bool restsUnattached = volume.Transition == null
                            && volume.CurrentAttachedVmId == null
                            && volume.DesiredAttachedVmId == null
                            && !volume.CurrentAttachedElsewhere;
        bool alreadyDetachingOrNotAttached = detachIsWaiting || restsUnattached;
        if (alreadyDetachingOrNotAttached)
        {
            return;
        }

        string externalVolumeId = volume.Detach();

        activity.Stage("volume.detach_requested", "volume", volume.Id, volume.Name); // atomic with Detach
        string projectName = TenantAggregate.ProjectNameFor(volume.TenantId);
        await commands.EnqueueVolumeDetachAsync(volume.Id, externalVolumeId, projectName, volume.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
