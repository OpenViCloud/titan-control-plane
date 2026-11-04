using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Application.Commands.Compute.Vm;

// The worker's report that a VM request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant: there must never be a route to this handler
// from a customer path. The row is locked so a request made meanwhile is ordered before or after this report, never
// interleaved (titan-ops architecture/resource-lifecycle.md#one-key-per-request). A report the VM is not waiting on is
// ignored and still answers 204: the worker has nothing to retry.
public class EndVmRequestCommandHandler(
    IVmRepository repo,
    IVolumeRepository volumeRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(EndVmRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the VM is loaded: a malformed report is the worker's bug, whatever the VM.
        OperationOutcome outcome = VmReports.ParseOutcome(command.Outcome);
        VmObservation? observed = VmReports.ParseObservation(command.Observed);

        Domain.Aggregates.Vm.Vm vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        VmOperationEnd end = new(outcome, command.ExternalId, observed, command.Reason, command.Detail);
        VmOperationEnded? ended = vm.EndOperation(command.RequestId, end);
        if (ended == null)
        {
            return;
        }

        await VmReports.RecordEndedAsync(vm, ended, outcome, activity, volumeRepo, ct);
        await repo.SaveAsync(ct);
    }
}
