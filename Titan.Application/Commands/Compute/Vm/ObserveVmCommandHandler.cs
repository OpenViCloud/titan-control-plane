using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Compute.Vm;

// The reconciler's observation of one VM (titan-ops architecture/resource-lifecycle.md#callback-routes): what Incus has,
// recorded as the Incus word (the status follows it only while no request waits), ending no request: only the report carrying a request's id ends it
// (titan-ops architecture/resource-lifecycle.md#invariants). Authenticated by X-Internal-Token at the endpoint, acting
// for no tenant. Locked like every lifecycle handler, so an observation and a request on the same VM take turns.
public class ObserveVmCommandHandler(IVmRepository repo)
{
    public async Task HandleAsync(ObserveVmCommand command, CancellationToken ct = default)
    {
        if (command.Exists && command.Observed == null)
        {
            throw new BadRequestException("INVALID_STATUS", "Observed is required when the instance exists.");
        }
        VmStatus? observedStatus = VmReports.ParseObservedStatus(command.Observed);

        Domain.Aggregates.Vm.Vm vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        // Exists without an observation was refused above, so an existing instance always has a status here.
        if (command.Exists && observedStatus != null)
        {
            vm.MarkObserved(observedStatus.Value, VmReports.ObservedIpAddress(command.Observed),
                VmReports.ObservedPublicIpv6Address(command.Observed));
        }
        else
        {
            vm.MarkNotFound();
        }

        await repo.SaveAsync(ct);
    }
}
