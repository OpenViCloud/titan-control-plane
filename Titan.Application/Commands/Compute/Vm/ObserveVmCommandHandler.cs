using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Compute.Vm;

// The reconciler's observation of one VM (titan-ops architecture/resource-lifecycle.md#callback-routes): what Incus has,
// written to current, ending no request: only the report carrying a request's id ends it
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
        VmObservation? observed = VmReports.ParseObservation(command.Observed);

        Domain.Aggregates.Vm.Vm vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        if (command.Exists)
        {
            vm.MarkObserved(observed!);
        }
        else
        {
            vm.MarkNotFound();
        }

        await repo.SaveAsync(ct);
    }
}
