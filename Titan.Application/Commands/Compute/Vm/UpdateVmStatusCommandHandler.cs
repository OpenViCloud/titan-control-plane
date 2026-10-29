using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Application.Commands.Compute.Vm.Contracts;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;

namespace Titan.Application.Commands.Compute.Vm;


// The worker's callback: an observation of what the provider already did, not an intent
// (titan-ops architecture/intent-and-observation.md#intent-versus-observation), so it is a command though nobody
// clicks it.
//
// No ICurrentUser on purpose: the reconciler acts for no tenant. The caller is authenticated by
// X-Internal-Token at the endpoint, so there is no ownership check here and there must never be a route
// to this handler from a customer path.
//
// Outcomes are recorded here (titan-ops features/activity/design.md#request-and-outcome-names), once per request:
// the aggregate says which waiting intent the observation fulfilled, so Running reported every cycle, or a
// guest-OS shutdown nobody asked for, fulfils nothing.
public class UpdateVmStatusCommandHandler(
    IVmRepository repo,
    IVmSnapshotRepository snapshotRepo,
    IVolumeRepository volumeRepo,
    IActivityRecorder activity)
{
    private static readonly Dictionary<VmIntent, string> OutcomeAction = new()
    {
        [VmIntent.Create] = "vm.created",
        [VmIntent.Start] = "vm.started",
        [VmIntent.Stop] = "vm.stopped",
        [VmIntent.Delete] = "vm.deleted",
    };

    public async Task HandleAsync(UpdateVmStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)
        var vmId = command.VmId;

        // Parsed before the VM is loaded: a malformed report is the worker's bug, whatever the VM.
        VmState? observedState = null;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                if (req.Exists == true)
                {
                    observedState = ParseIncusStatus(req.Status);
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
                throw new BadRequestException("INVALID_STATUS", $"Unknown VM report: '{req.Report}'");
        }

        var vm = await repo.FindByIdAsync(vmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        VmIntent? fulfilled = null;
        switch (req.Report)
        {
            case "Observed":
                if (observedState == null)
                {
                    vm.MarkNotFound();
                }
                else
                {
                    fulfilled = vm.MarkObserved(observedState.Value, req.IpAddress, req.PublicIpv6Address);
                }
                break;
            case "Created":
                vm.MarkCreated(req.ExternalId!);
                break;
            case "RequestFailed":
                // The give-up report carries the provider id when Incus had created the instance and the Created
                // report never arrived; without it the delete would forget a VM that still runs.
                bool carriesAnIdNotYetStored = !string.IsNullOrEmpty(req.ExternalId) && vm.ExternalId == null;
                if (carriesAnIdNotYetStored)
                {
                    vm.MarkCreated(req.ExternalId!);
                }
                // A failed create records nothing: the request was the create, and a failure is not its outcome.
                vm.MarkRequestFailed(req.Reason, req.Detail);
                break;
            case "Deleted":
                fulfilled = vm.MarkDeleted();
                await snapshotRepo.MarkAllDeletedByVmIdAsync(vmId, ct);
                // The provider kept the VM's volumes and detached them (titan-ops features/volume/design.md#vm-delete-releases-volumes).
                List<VolumeAggregate> attachedVolumes = await volumeRepo.ListAttachedToVmAsync(vmId, ct);
                foreach (VolumeAggregate volume in attachedVolumes)
                {
                    volume.MarkDetachedByVmDelete(vmId);
                }
                break;
        }

        if (fulfilled != null)
        {
            activity.StageOutcome(vm.TenantId, OutcomeAction[fulfilled.Value], "vm", vm.Id, vm.Name);
        }

        await repo.SaveAsync(ct);
    }

    // The worker sends the Incus status word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    // Matched by name, exactly: Enum.TryParse would also take "1" or "running".
    private static VmState ParseIncusStatus(string? status)
    {
        foreach (VmState known in Enum.GetValues<VmState>())
        {
            if (known.ToString() == status)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown VM status: '{status}'");
    }
}
