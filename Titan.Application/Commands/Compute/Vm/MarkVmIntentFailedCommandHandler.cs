using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Application.Commands.Compute.Vm;

// The old give-up route of vm.stop / vm.start, alive until rollout phase 6
// (titan-ops architecture/resource-lifecycle.md#callback-routes): the route names the request, the body carries no
// reason, so the fixed reason of the request's kind is recorded. The same ending as a Failed on the request route.
public class MarkVmIntentFailedCommandHandler(
    IVmRepository repo,
    IVolumeRepository volumeRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(MarkVmIntentFailedCommand command, CancellationToken ct = default)
    {
        Domain.Aggregates.Vm.Vm vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        PendingOperation<VmOperationKind>? waiting = vm.Pending;
        bool namesTheWaitingRequest = waiting != null && waiting.Id == command.IntentId;
        if (!namesTheWaitingRequest)
        {
            return;
        }

        PendingOperation<VmOperationKind>? ended = await VmReports.FailOperationAsync(vm, command.IntentId, null, null,
            VmReports.ReasonFor(waiting!.Kind), null, activity, volumeRepo, ct);
        if (ended == null)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }
}
