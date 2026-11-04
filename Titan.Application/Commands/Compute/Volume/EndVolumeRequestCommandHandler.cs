using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

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
        RequestOutcome outcome = VolumeReports.ParseOutcome(command.Outcome);

        Domain.Aggregates.Volume.Volume volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        // A create that ends Done must name the volume, or it would be left with nothing to delete through.
        bool endsTheCreateWithoutAnId = outcome == RequestOutcome.Done
                                        && volume.Pending is { Kind: VolumeRequestKind.Create } waiting
                                        && waiting.Id == command.RequestId
                                        && volume.ExternalId == null
                                        && string.IsNullOrEmpty(command.ExternalId);
        if (endsTheCreateWithoutAnId)
        {
            throw new BadRequestException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }

        VolumeObservation? observed = command.AttachedTo == null
            ? null
            : await VolumeReports.ResolveAttachmentAsync(vmRepo, volume.TenantId, command.AttachedTo, ct);
        VolumeRequestEnd end = new(outcome, command.ExternalId, observed, command.Reason, command.Detail);
        VolumeRequestEnded? ended = volume.EndRequest(command.RequestId, end);
        if (ended == null)
        {
            return;
        }

        VolumeReports.RecordEnded(volume, ended, outcome, activity);
        await repo.SaveAsync(ct);
    }
}
