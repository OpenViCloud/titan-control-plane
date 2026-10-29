using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories.Compute;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Application.Commands.Compute.Vm;

// The write half of opening an SSH session (titan-ops features/vm/design.md#ssh-session): who asked, recorded before the
// endpoint reaches Incus. The session itself changes nothing on the VM.
public class RequestVmSshCommandHandler(IVmRepository repo, ICurrentUser currentUser, IActivityRecorder activity)
{
    public async Task HandleAsync(RequestVmSshCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        Domain.Aggregates.Compute.Vm.Vm vm = await ComputeOwnership.RequireVmAsync(repo, tenantId, command.VmId, ct);
        vm.ShellInstanceName();
        await activity.RecordAsync("vm.ssh_requested", "vm", vm.Id, vm.Name, ct);
    }
}
