using Titan.Domain.Values;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Volume;

public class DeleteVolumeCommandHandler(
    IVolumeRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteVolumeCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var volume = await ComputeOwnership.RequireVolumeForUpdateAsync(repo, callerId, command.VolumeId, ct);

        // A repeat is answered like the first request: nothing recorded, nothing sent
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A delete that failed is not a
        // repeat: it falls through and the aggregate decides.
        if (volume.IsDeletedOrDeleting)
        {
            return;
        }

        // The volume refuses while a snapshot it holds still counts (titan-ops features/volume/design.md#delete-refused-with-snapshots).
        // A volume that never reached Incus is deleted here and gets no ticket
        // (titan-ops architecture/desired-and-current.md#delete-locally): the request and its outcome at once.
        OperationTicket? ticket = volume.Delete();

        activity.Stage("volume.delete_requested", "volume", volume.Id, volume.Name); // atomic with Delete
        if (ticket == null)
        {
            activity.StageOutcome(volume.TenantId, "volume.deleted", "volume", volume.Id, volume.Name);
            await repo.SaveAsync(ct);
            return;
        }
        await commands.EnqueueVolumeDeleteAsync(volume, ticket.OperationId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(volume.TenantId), ct);
        await repo.SaveAsync(ct);
    }
}
