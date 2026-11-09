using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Commands.Compute.Vm.Contracts;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.Vm;


// The mirror of StopVm: the VM moves to Starting, and the start command's own report ends the request and records
// "vm.started".
public class StartVmCommandHandler(
    IVmRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(StartVmCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        // Locked, as StopVmCommandHandler (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        var vm = await ComputeOwnership.RequireVmForUpdateAsync(repo, callerId, command.VmId, ct);
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A start still waiting, or already
        // running (Ready is running with a guest that said so) and nothing else asked of it: answer like the first
        // request (titan-ops architecture/resource-lifecycle.md#repeat).
        if (vm.IsStartWaitingOrRunning)
        {
            return;
        }

        Guid operationId = Guid.NewGuid();
        string externalVmId = vm.Start(operationId);

        activity.Stage("vm.start_requested", "vm", vm.Id, vm.Name);
        await commands.EnqueueVmStartAsync(vm.Id, operationId, externalVmId, projectName, vm.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
