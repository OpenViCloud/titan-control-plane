using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Repositories.Compute;
using Titan.Application.Commands.Compute.Vm.Contracts;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Vm;


// The mirror of StopVm: the reconciler observes RUNNING, reports Running, and that observation fulfils
// the start intent — nothing here writes a status.
public class StartVmCommandHandler(
    IVmRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(StartVmCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vm = await ComputeOwnership.RequireVmAsync(repo, callerId, command.VmId, ct);
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // A start still waiting: enqueue it again with the SAME intent id (see StopVmCommandHandler).
        bool startIsWaiting = vm.PendingIntent == VmIntent.Start;
        if (startIsWaiting)
        {
            VmIntentTicket waiting = vm.ResendWaitingIntent(VmIntent.Start);
            await commands.EnqueueVmStartAsync(vm.Id, waiting.IntentId, waiting.ProviderId, projectName, vm.ClusterId, ct);
            await repo.SaveAsync(ct);
            return;
        }

        // Already running (Ready is running with a guest that said so) and nothing else asked of it: answer
        // like the first request.
        bool instanceRuns = vm.CurrentState == VmState.Running || vm.CurrentState == VmState.Ready;
        bool alreadyRunning = vm.PendingIntent == null && instanceRuns;
        if (alreadyRunning)
        {
            return;
        }

        VmIntentTicket ticket = vm.RequestStart();

        activity.Stage("vm.start_requested", "vm", vm.Id, vm.Name);
        await commands.EnqueueVmStartAsync(vm.Id, ticket.IntentId, ticket.ProviderId, projectName, vm.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
