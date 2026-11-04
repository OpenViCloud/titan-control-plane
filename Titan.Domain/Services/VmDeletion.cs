using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Domain.Services;

/// The one way a VM is deleted, by its tenant or by an admin: a VM a load balancer still sends to is not
/// (titan-ops architecture/domain-services.md#cross-aggregate-lock; titan-ops features/load-balancer/design.md#lb-and-vm-network).
/// The rule spans the VM and the load balancers, so it is a domain service; Vm.Delete and DeleteLocally are
/// internal, so no caller deletes without the check. The caller loads the VM with its row locked, so a load balancer
/// naming it as a backend waits for this call and then sees it.
public class VmDeletion(ILoadBalancerRepository loadBalancers)
{
    /// The VM's own refusals come first (already deleted, a snapshot being created or reverted). A refusal here is
    /// thrown before anything is saved, so the operation Delete recorded in memory goes with it. Null: the VM
    /// never reached the provider, so it was deleted locally and there is no command to enqueue.
    public async Task<OperationTicket?> DeleteAsync(Vm vm, CancellationToken ct = default)
    {
        OperationTicket? ticket = vm.Delete();
        if (await loadBalancers.IsBackendAsync(vm.Id, ct))
        {
            throw new BusinessException("VM_IS_LOAD_BALANCER_BACKEND",
                "A load balancer still sends to this VM. Remove it from the load balancer first.");
        }
        bool nothingOnTheProvider = ticket == null;
        if (nothingOnTheProvider)
        {
            vm.DeleteLocally();
        }
        return ticket;
    }
}
