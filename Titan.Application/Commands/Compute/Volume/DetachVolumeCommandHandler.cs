using Titan.Domain.Values;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

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
        var volume = await ComputeOwnership.RequireVolumeForUpdateAsync(repo, callerId, command.VolumeId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): answered like the first request,
        // nothing recorded, nothing sent.
        if (volume.IsDetachWaitingOrDetached)
        {
            return;
        }

        OperationTicket ticket = volume.Detach();

        activity.Stage("volume.detach_requested", "volume", volume.Id, volume.Name); // atomic with Detach
        await commands.EnqueueVolumeDetachAsync(volume, ticket.OperationId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(volume.TenantId), ct);
        await repo.SaveAsync(ct);
    }
}
