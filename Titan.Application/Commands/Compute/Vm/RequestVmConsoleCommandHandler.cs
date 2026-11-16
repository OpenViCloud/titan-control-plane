using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Application.Commands.Compute.Vm;

// The write half of opening a console (titan-ops features/vm/design.md#vm-console): who opened it, recorded once Incus
// attached the console. The console changes nothing on the VM.
public class RequestVmConsoleCommandHandler(IVmRepository repo, ICurrentUser currentUser, IActivityRecorder activity)
{
    public async Task HandleAsync(RequestVmConsoleCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        Domain.Aggregates.Vm.Vm vm = await ComputeOwnership.RequireVmAsync(repo, tenantId, command.VmId, ct);
        vm.EnsureShellCanOpen();
        await activity.RecordAsync("vm.console_requested", "vm", vm.Id, vm.Name, ct);
    }
}
