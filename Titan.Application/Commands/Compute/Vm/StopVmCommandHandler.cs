using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Commands.Compute.Vm.Contracts;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Vm;


// The CP records the request (Vm.Pending) and enqueues the provider command; it writes no status
// (titan-ops architecture/intent-and-observation.md#intent-has-identity). The command's own report ends the request and
// records the "vm.stopped" outcome (titan-ops architecture/resource-lifecycle.md#kinds), not this handler.
public class StopVmCommandHandler(
    IVmRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(StopVmCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        // Locked: two requests on one VM take turns, and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        var vm = await ComputeOwnership.RequireVmForUpdateAsync(repo, callerId, command.VmId, ct);
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A stop still waiting, or already
        // stopped with nothing else asked of it: answer like the first request, record and send nothing
        // (titan-ops architecture/resource-lifecycle.md#repeat).
        bool stopIsWaiting = vm.Pending?.Kind == VmRequestKind.Stop;
        bool alreadyStopped = vm.Pending == null && vm.CurrentState == VmState.Stopped;
        if (stopIsWaiting || alreadyStopped)
        {
            return;
        }

        // The guard stays on the aggregate (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id): any other state is refused there.
        RequestTicket ticket = vm.RequestStop();

        activity.Stage("vm.stop_requested", "vm", vm.Id, vm.Name);
        await commands.EnqueueVmStopAsync(vm.Id, ticket.RequestId, ticket.ProviderId, projectName, vm.ClusterId, ct);
        await repo.SaveAsync(ct); // request, request event and provider command together
    }
}
