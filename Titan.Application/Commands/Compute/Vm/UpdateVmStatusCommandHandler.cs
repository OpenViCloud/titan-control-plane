using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Compute.Vm;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): a
// worker that predates the request routes reports the reconciler's observation and the create and delete commands'
// results here, none of them naming a request. Each report is mapped onto the request the VM is waiting on, so the
// VM ends up as the new routes would leave it. No ICurrentUser on purpose: authenticated by X-Internal-Token at the
// endpoint, acting for no tenant.
public class UpdateVmStatusCommandHandler(
    IVmRepository repo,
    IVolumeRepository volumeRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateVmStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before the VM is loaded: a malformed report is the worker's bug, whatever the VM.
        VmObservation? observed = null;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                if (req.Exists == true)
                {
                    observed = new VmObservation(VmReports.ParseIncusStatus(req.Status), req.IpAddress, req.PublicIpv6Address);
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

        Domain.Aggregates.Vm.Vm vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        PendingOperation<VmOperationKind>? waiting = vm.Pending;
        VmOperationEnded? ended = null;
        OperationOutcome outcome = OperationOutcome.Done;
        switch (req.Report)
        {
            case "Observed":
                if (observed == null)
                {
                    vm.MarkNotFound();
                }
                else
                {
                    vm.MarkObserved(observed);
                }
                // Temporary allowlist, rollout phase 6 removes it (VmReports.EndStopOrStartTheObservationReached).
                ended = VmReports.EndStopOrStartTheObservationReached(vm);
                break;
            case "Created":
                // The old create command reports the instance name as soon as Incus holds it: that is the create's Done
                // (titan-ops architecture/resource-lifecycle.md#kinds). Running comes later as an observation.
                if (waiting is { Kind: VmOperationKind.Create })
                {
                    ended = vm.EndOperation(waiting.Id, VmOperationEnd.Done(req.ExternalId, null));
                }
                break;
            case "RequestFailed":
                // The give-up carries the provider id when Incus had created the instance and the Created report never
                // arrived; without it the delete would forget a VM that still runs.
                outcome = OperationOutcome.Failed;
                if (waiting is { Kind: VmOperationKind.Create })
                {
                    ended = vm.EndOperation(waiting.Id, VmOperationEnd.Failed(req.ExternalId, null, req.Reason, req.Detail));
                }
                break;
            case "Deleted":
                // The delete command saw the instance gone after Titan's own delete. With no delete waiting the VM is
                // still gone from Incus, which is what NotFound records; only a delete's own report ends a VM.
                if (waiting is { Kind: VmOperationKind.Delete })
                {
                    ended = vm.EndOperation(waiting.Id, VmOperationEnd.Done(null, null));
                }
                else
                {
                    vm.MarkNotFound();
                }
                break;
        }

        if (ended != null)
        {
            await VmReports.RecordEndedAsync(vm, ended, outcome, activity, volumeRepo, ct);
        }

        await repo.SaveAsync(ct);
    }
}
