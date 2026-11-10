using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Application.Commands.Compute.VmSnapshot;

// The reconciler's observation of one VM snapshot (titan-ops architecture/resource-lifecycle.md#callback-routes): no
// request ends here. Authenticated by X-Internal-Token at the endpoint; the VM's row is locked like every lifecycle
// handler's. A snapshot the VM does not have answers 404 from the VM itself (Vm.GetSnapshot).
public class ObserveVmSnapshotCommandHandler(IVmRepository repo)
{
    public async Task HandleAsync(ObserveVmSnapshotCommand command, CancellationToken ct = default)
    {
        VmAggregate vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        if (command.Exists)
        {
            vm.MarkSnapshotObserved(command.SnapshotId);
        }
        else
        {
            vm.MarkSnapshotNotFound(command.SnapshotId);
        }

        await repo.SaveAsync(ct);
    }
}
