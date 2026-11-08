using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Application.Commands.Compute.Volume;

// The reconciler's observation of one volume (titan-ops architecture/resource-lifecycle.md#callback-routes): what Incus has,
// recorded without ending a request; no request ends here (titan-ops architecture/resource-lifecycle.md#invariants). Authenticated by
// X-Internal-Token at the endpoint, acting for no tenant. Locked like every lifecycle handler, so an observation and a
// request on the same volume take turns.
public class ObserveVolumeCommandHandler(
    IVolumeRepository repo,
    IVmRepository vmRepo)
{
    public async Task HandleAsync(ObserveVolumeCommand command, CancellationToken ct = default)
    {
        Domain.Aggregates.Volume.Volume volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        if (command.Exists)
        {
            (Guid? attachedVmId, bool attachedElsewhere) =
                await VolumeReports.ResolveAttachmentAsync(vmRepo, volume.TenantId, command.AttachedTo, ct);
            volume.MarkObserved(attachedVmId, attachedElsewhere);
        }
        else
        {
            volume.MarkNotFound();
        }

        await repo.SaveAsync(ct);
    }
}
