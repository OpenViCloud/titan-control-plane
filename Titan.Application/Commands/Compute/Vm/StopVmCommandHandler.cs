using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Commands.Compute.Vm.Contracts;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Vm;


// The VM moves to Stopping with a new request id (titan-ops features/vm/design.md#vm-status) and the provider command is
// enqueued (titan-ops architecture/intent-and-observation.md#intent-has-identity). The command's own report ends the request and
// records the "vm.stopped" outcome (titan-ops architecture/resource-lifecycle.md#kinds); not this handler.
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
        if (vm.IsStopWaitingOrStopped)
        {
            return;
        }

        // The guard stays on the aggregate: any other status is refused there, and an accepted stop hands back the
        // instance name (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
        Guid operationId = Guid.NewGuid();
        string externalVmId = vm.Stop(operationId);

        activity.Stage("vm.stop_requested", "vm", vm.Id, vm.Name);
        await commands.EnqueueVmStopAsync(vm.Id, operationId, externalVmId, projectName, vm.ClusterId, ct);
        await repo.SaveAsync(ct); // request, request event and provider command together
    }
}
