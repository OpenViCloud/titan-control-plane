using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Repositories.Compute;
using Titan.Application.Commands.Compute.Vm.Contracts;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Vm;


// The CP records the request (Vm.PendingIntent) and enqueues the provider command; it writes no status
// (titan-ops architecture/intent-and-observation.md#intent-has-identity). The reconciler observes POWEROFF and
// reports Stopped, and UpdateVmStatusCommandHandler records the "vm.stopped" outcome there, not here.
public class StopVmCommandHandler(
    IVmRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(StopVmCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vm = await ComputeOwnership.RequireVmAsync(repo, callerId, command.VmId, ct);
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // A stop still waiting: enqueue it again with the SAME intent id. The command row absorbs it while
        // open or after it succeeded, and reopens only if it failed
        // (titan-ops architecture/provider-and-worker.md#provider-command-table); the save persists the reopen.
        bool stopIsWaiting = vm.PendingIntent == VmIntent.Stop;
        if (stopIsWaiting)
        {
            VmIntentTicket waiting = vm.ResendWaitingIntent(VmIntent.Stop);
            await commands.EnqueueVmStopAsync(vm.Id, waiting.IntentId, waiting.ProviderId, projectName, vm.ClusterId, ct);
            await repo.SaveAsync(ct);
            return;
        }

        // Already stopped and nothing else asked of it: answer like the first request.
        bool alreadyStopped = vm.PendingIntent == null && vm.CurrentState == VmState.Stopped;
        if (alreadyStopped)
        {
            return;
        }

        // The guard stays on the aggregate (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id): any other state is refused there.
        VmIntentTicket ticket = vm.RequestStop();

        activity.Stage("vm.stop_requested", "vm", vm.Id, vm.Name);
        await commands.EnqueueVmStopAsync(vm.Id, ticket.IntentId, ticket.ProviderId, projectName, vm.ClusterId, ct);
        await repo.SaveAsync(ct); // intent, request event and provider command together
    }
}
