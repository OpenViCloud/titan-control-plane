using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Services;

namespace Titan.Application.Commands.Compute.Volume;

// The worker's report that a volume request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant: there must never be a route to this handler
// from a customer path. The row is locked so a request made meanwhile is ordered before or after this report, never
// interleaved (titan-ops architecture/resource-lifecycle.md#one-key-per-request). A report the volume is not waiting on is
// ignored and still answers 204: the worker has nothing to retry.
public class EndVolumeRequestCommandHandler(
    IVolumeRepository repo,
    IVmRepository vmRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(EndVolumeRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the volume is loaded: a malformed report is the worker's bug, whatever the volume.
        OperationOutcome outcome = VolumeReports.ParseOutcome(command.Outcome);

        Domain.Aggregates.Volume.Volume volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        // A report without the instance list did not read the volume on Incus.
        bool sawTheVolume = command.AttachedTo != null;
        Guid? attachedVmId = null;
        bool attachedElsewhere = false;
        if (sawTheVolume)
        {
            (attachedVmId, attachedElsewhere) =
                await VolumeReports.ResolveAttachmentAsync(vmRepo, volume.TenantId, command.AttachedTo, ct);
        }
        VolumeStatus? ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = VolumeReports.FinishOperation(volume, command.RequestId, command.ExternalId,
                sawTheVolume: sawTheVolume, attachedVmId: attachedVmId, attachedElsewhere: attachedElsewhere, activity);
        }
        else
        {
            ended = VolumeReports.FailOperation(volume, command.RequestId, command.ExternalId,
                sawTheVolume: sawTheVolume, attachedVmId: attachedVmId, attachedElsewhere: attachedElsewhere,
                reason: command.Reason, detail: command.Detail, activity);
        }
        if (ended == null)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }
}
