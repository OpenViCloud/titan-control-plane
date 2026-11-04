using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Application.Commands.Compute.Volume;

// The reconciler's observation of one volume (titan-ops architecture/resource-lifecycle.md#callback-routes): what Incus has,
// written to current (titan-ops architecture/resource-lifecycle.md#invariants), and until rollout phase 6 the one
// temporary exception below. Authenticated by X-Internal-Token at the endpoint, acting for no tenant. Locked like every
// lifecycle handler, so an observation and a request on the same volume take turns.
public class ObserveVolumeCommandHandler(
    IVolumeRepository repo,
    IVmRepository vmRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(ObserveVolumeCommand command, CancellationToken ct = default)
    {
        Domain.Aggregates.Volume.Volume volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        if (command.Exists)
        {
            VolumeObservation observed = await VolumeReports.ResolveAttachmentAsync(vmRepo, volume.TenantId, command.AttachedTo, ct);
            volume.MarkObserved(observed.AttachedVmId, observed.AttachedElsewhere);
        }
        else
        {
            volume.MarkNotFound();
        }

        // Temporary allowlist, rollout phase 6 removes it (VolumeReports.EndAttachOrDetachTheObservationReached).
        VolumeOperationEnded? ended = VolumeReports.EndAttachOrDetachTheObservationReached(volume);
        if (ended != null)
        {
            VolumeReports.RecordEnded(volume, ended, OperationOutcome.Done, activity);
        }

        await repo.SaveAsync(ct);
    }
}
