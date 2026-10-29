using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;

namespace Titan.Application.Commands.Compute.Vm;

// A worker callback like UpdateVmStatusCommandHandler: an observation (the provider refused the action on
// every retry), authenticated by X-Internal-Token at the endpoint, acting for no user. Without it a failed
// stop would leave its intent waiting forever (titan-ops features/vm/design.md#intent-failed-callback).
//
// The failure is the request's outcome (titan-ops features/activity/design.md#request-and-outcome-names), recorded
// once: the aggregate returns the intent only when it actually cleared it.
public class MarkVmIntentFailedCommandHandler(IVmRepository repo, IActivityRecorder activity)
{
    private static readonly Dictionary<VmIntent, string> FailedAction = new()
    {
        [VmIntent.Stop] = "vm.stop_failed",
        [VmIntent.Start] = "vm.start_failed",
    };

    public async Task HandleAsync(MarkVmIntentFailedCommand command, CancellationToken ct = default)
    {
        Domain.Aggregates.Compute.Vm.Vm? vm = await repo.FindByIdAsync(command.VmId, ct);
        if (vm == null)
        {
            throw new NotFoundException("VM_NOT_FOUND", "VM not found.");
        }

        VmIntent? failed = vm.AbandonRequest(command.IntentId);
        bool clearedAWaitingRequest = failed != null;
        if (!clearedAWaitingRequest)
        {
            return;
        }

        activity.StageOutcome(vm.TenantId, FailedAction[failed!.Value], "vm", vm.Id, vm.Name);
        await repo.SaveAsync(ct);
    }
}
