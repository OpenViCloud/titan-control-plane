using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way a VM is deleted, by its tenant or by an admin: a VM a load balancer still sends to is not
/// (titan-ops architecture/domain-services.md#cross-aggregate-lock; titan-ops features/load-balancer/design.md#lb-and-vm-network).
/// The rule spans the VM and the load balancers, so it is a domain service; Vm.Delete is internal, so no caller
/// deletes without the check. The caller loads the VM with its row locked, so a load balancer naming it as a backend
/// waits for this call and then sees it.
public class VmDeletion(ILoadBalancerRepository loadBalancers)
{
    /// The VM's own refusals come first (already deleted, a snapshot being created or reverted). A refusal here is
    /// thrown before anything is saved, so the status Delete wrote in memory goes with it. Afterwards the VM's status
    /// tells the caller what happened: Deleting waits on a command under operationId and the instance name comes back
    /// for it; Deleted never reached the provider, is gone already, and hands back null
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    public async Task<string?> DeleteAsync(Vm vm, Guid operationId, CancellationToken ct = default)
    {
        string? instance = vm.Delete(operationId);
        if (await loadBalancers.IsBackendAsync(vm.Id, ct))
        {
            throw new BusinessException("VM_IS_LOAD_BALANCER_BACKEND",
                "A load balancer still sends to this VM. Remove it from the load balancer first.");
        }
        return instance;
    }
}
