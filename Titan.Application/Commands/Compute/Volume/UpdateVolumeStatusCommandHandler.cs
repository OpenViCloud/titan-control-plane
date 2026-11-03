using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Application.Commands.Compute.Volume;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): a
// worker that predates the request routes reports the reconciler's observation and the commands' results here, none of
// them naming a request. Each report is mapped onto the request the volume is waiting on, so the volume ends up as the
// new routes would leave it. No ICurrentUser on purpose: authenticated by X-Internal-Token at the endpoint, acting for
// no tenant.
public class UpdateVolumeStatusCommandHandler(
    IVolumeRepository repo,
    IVmRepository vmRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateVolumeStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before the volume is loaded: a malformed report is the worker's bug, whatever the volume.
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(req.ExternalId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalId is required when report is Created.");
                }
                break;
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown volume report: '{req.Report}'");
        }

        Domain.Aggregates.Volume.Volume volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        PendingRequest<VolumeRequestKind>? waiting = volume.Pending;
        VolumeRequestEnded? ended = null;
        RequestOutcome outcome = RequestOutcome.Done;
        switch (req.Report)
        {
            case "Observed":
                await ObserveAsync(volume, req.Exists!.Value, req.AttachedTo, ct);
                // Temporary allowlist, rollout phase 6 removes it (VolumeReports.EndAttachOrDetachTheObservationReached):
                // the old attach and detach commands report themselves this way.
                ended = VolumeReports.EndAttachOrDetachTheObservationReached(volume);
                break;
            case "Created":
                // The old create and restore commands report the volume name as soon as Incus holds it: the create's Done.
                if (waiting is { Kind: VolumeRequestKind.Create })
                {
                    ended = volume.EndRequest(waiting.Id, VolumeRequestEnd.Done(req.ExternalId, null));
                }
                break;
            case "RequestFailed":
                // An attach's or detach's give-up carries what Incus showed: one that happened while its report failed
                // is ended Done by the observation, as before; otherwise the waiting request ends Failed, keeping an id
                // the create's give-up found.
                if (req.Exists != null)
                {
                    await ObserveAsync(volume, req.Exists.Value, req.AttachedTo, ct);
                    ended = VolumeReports.EndAttachOrDetachTheObservationReached(volume);
                }
                PendingRequest<VolumeRequestKind>? stillWaiting = volume.Pending;
                if (ended == null && stillWaiting != null)
                {
                    outcome = RequestOutcome.Failed;
                    ended = volume.EndRequest(stillWaiting.Id,
                        VolumeRequestEnd.Failed(req.ExternalId, null, req.Reason, req.Detail));
                }
                break;
            case "Deleted":
                // Only a delete's own report ends a volume; with no delete waiting the volume is gone from Incus, which
                // is what NotFound records.
                if (waiting is { Kind: VolumeRequestKind.Delete })
                {
                    ended = volume.EndRequest(waiting.Id, VolumeRequestEnd.Done(null, null));
                }
                else
                {
                    volume.MarkNotFound();
                }
                break;
        }

        if (ended != null)
        {
            VolumeReports.RecordEnded(volume, ended, outcome, activity);
        }

        await repo.SaveAsync(ct);
    }

    private async Task ObserveAsync(Domain.Aggregates.Volume.Volume volume, bool exists, List<string>? attachedTo,
        CancellationToken ct)
    {
        if (!exists)
        {
            volume.MarkNotFound();
            return;
        }
        VolumeObservation observed = await VolumeReports.ResolveAttachmentAsync(vmRepo, volume.TenantId, attachedTo, ct);
        volume.MarkObserved(observed.AttachedVmId, observed.AttachedElsewhere);
    }
}
