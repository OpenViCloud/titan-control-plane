using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using VmAggregate = Titan.Domain.Aggregates.Compute.Vm.Vm;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;
using VolumeSnapshotAggregate = Titan.Domain.Aggregates.Compute.VolumeSnapshot.VolumeSnapshot;

namespace Titan.Application.Commands.Compute.Volume;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation:
// observation converges). No ICurrentUser: the reconciler acts for no tenant, and the caller is authenticated by
// X-Internal-Token at the endpoint — which is why there is no ownership check here and why no customer path may ever
// route to it. Outcomes are recorded once: the aggregate says whether a report fulfilled the waiting request
// (titan-ops features/activity/design.md#outcome-source).
public class UpdateVolumeStatusCommandHandler(
    IVolumeRepository repo,
    IVolumeSnapshotRepository snapshotRepo,
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

        var volume = await repo.FindByIdAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
                outcome = await ApplyObservationAsync(volume, req.Exists!.Value, req.AttachedTo, ct);
                break;
            case "Created":
                outcome = CreatedOutcome(volume.MarkCreated(req.ExternalId!));
                break;
            case "RequestFailed":
                // A create's give-up carrying the id found the volume on Incus: the create happened.
                bool carriesAnIdNotYetStored = !string.IsNullOrEmpty(req.ExternalId) && volume.ExternalId == null;
                if (carriesAnIdNotYetStored)
                {
                    outcome = CreatedOutcome(volume.MarkCreated(req.ExternalId!));
                }
                // An attach's or detach's give-up carries what Incus showed: an attach that happened while its report
                // failed is fulfilled by it, and is not abandoned below.
                if (req.Exists != null)
                {
                    string? observedOutcome = await ApplyObservationAsync(volume, req.Exists.Value, req.AttachedTo, ct);
                    if (observedOutcome != null)
                    {
                        outcome = observedOutcome;
                    }
                }
                // A request Incus refused moves the desired pair back to what Incus holds
                // (titan-ops architecture/desired-and-current.md#abandoned-request); a create's give-up only records why.
                bool attachDetachOrDeleteWaiting = volume.Transition == VolumeTransition.Attaching
                                                   || volume.Transition == VolumeTransition.Detaching
                                                   || volume.Transition == VolumeTransition.Deleting;
                if (attachDetachOrDeleteWaiting)
                {
                    volume.AbandonRequest(req.Reason, req.Detail);
                }
                else
                {
                    volume.MarkRequestFailed(req.Reason, req.Detail);
                }
                break;
            case "Deleted":
                bool deletedNow = volume.MarkDeleted();
                if (deletedNow)
                {
                    outcome = "volume.deleted";
                }
                // Incus deletes a volume's snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots).
                // A snapshot whose own delete was waiting is done here: its delete command will only find it already
                // Deleted, so its outcome is recorded now.
                List<VolumeSnapshotAggregate> snapshots = await snapshotRepo.ListLiveOfVolumeForUpdateAsync(volume.Id, ct);
                foreach (VolumeSnapshotAggregate snapshot in snapshots)
                {
                    bool snapshotDeleteFulfilled = snapshot.MarkDeleted();
                    if (snapshotDeleteFulfilled)
                    {
                        activity.StageOutcome(snapshot.TenantId, "volume_snapshot.deleted", "volume_snapshot",
                            snapshot.Id, snapshot.Name);
                    }
                }
                break;
        }

        if (outcome != null)
        {
            activity.StageOutcome(volume.TenantId, outcome, "volume", volume.Id, volume.Name);
        }

        await repo.SaveAsync(ct);
    }

    private async Task<string?> ApplyObservationAsync(
        VolumeAggregate volume, bool exists, List<string>? attachedTo, CancellationToken ct)
    {
        if (!exists)
        {
            volume.MarkNotFound();
            return null;
        }
        VolumeAttachment attachment = await ResolveAttachmentAsync(volume.TenantId, attachedTo, ct);
        VolumeTransition? fulfilled = volume.MarkObserved(attachment.VmId, attachment.Elsewhere);
        if (fulfilled == VolumeTransition.Creating)
        {
            return "volume.created";
        }
        if (fulfilled == VolumeTransition.Attaching)
        {
            return "volume.attached";
        }
        if (fulfilled == VolumeTransition.Detaching)
        {
            return "volume.detached";
        }
        return null;
    }

    // The worker reports instance names as Incus lists them; which of them are Titan VMs is Titan's to say
    // (titan-ops features/volume/design.md#attachment-from-used-by). Only the volume's own tenant is searched: an instance
    // name of another tenant's VM must never read as this volume's VM. A name matching no VM, or a second VM, is an
    // instance Titan does not manage for this volume.
    private async Task<VolumeAttachment> ResolveAttachmentAsync(
        Guid tenantId, List<string>? instanceNames, CancellationToken ct)
    {
        bool noInstance = instanceNames == null || instanceNames.Count == 0;
        if (noInstance)
        {
            return new VolumeAttachment(null, false);
        }
        List<VmAggregate> vms = await vmRepo.ListByExternalIdsAsync(tenantId, instanceNames!, ct);
        Guid? attachedVmId = null;
        bool elsewhere = false;
        foreach (string instanceName in instanceNames!)
        {
            VmAggregate? vm = vms.FirstOrDefault(v => v.ExternalId == instanceName);
            bool isTheFirstTitanVm = vm != null && attachedVmId == null;
            if (isTheFirstTitanVm)
            {
                attachedVmId = vm!.Id;
            }
            else
            {
                elsewhere = true;
            }
        }
        return new VolumeAttachment(attachedVmId, elsewhere);
    }

    private sealed record VolumeAttachment(Guid? VmId, bool Elsewhere);

    private static string? CreatedOutcome(bool fulfilledTheCreate)
    {
        if (fulfilledTheCreate)
        {
            return "volume.created";
        }
        return null;
    }
}
