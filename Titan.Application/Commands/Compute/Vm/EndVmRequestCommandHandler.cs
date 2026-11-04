using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

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
        RequestOutcome outcome = VmReports.ParseOutcome(command.Outcome);
        VmObservation? observed = VmReports.ParseObservation(command.Observed);

        Domain.Aggregates.Vm.Vm vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        // A create that ends Done must name the instance, or the VM would be left with nothing to delete through.
        bool endsTheCreateWithoutAnId = outcome == RequestOutcome.Done
                                        && vm.Pending is { Kind: VmRequestKind.Create } waiting
                                        && waiting.Id == command.RequestId
                                        && vm.ExternalId == null
                                        && string.IsNullOrEmpty(command.ExternalId);
        if (endsTheCreateWithoutAnId)
        {
            throw new BadRequestException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }

        VmRequestEnd end = new(outcome, command.ExternalId, observed, command.Reason, command.Detail);
        VmRequestEnded? ended = vm.EndRequest(command.RequestId, end);
        if (ended == null)
        {
            return;
        }

        await VmReports.RecordEndedAsync(vm, ended, outcome, activity, volumeRepo, ct);
        await repo.SaveAsync(ct);
    }
}
