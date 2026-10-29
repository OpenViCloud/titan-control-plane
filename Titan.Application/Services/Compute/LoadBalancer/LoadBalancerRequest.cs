using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using VmAggregate = Titan.Domain.Aggregates.Compute.Vm.Vm;

namespace Titan.Application.Services.Compute.LoadBalancer;

/// What the create and the update both read from a request (handlers do not call handlers).
public static class LoadBalancerRequest
{
    /// The validator has refused any protocol but TCP and UDP.
    public static List<LoadBalancerListenerSpec> Listeners(List<LoadBalancerListenerInput> inputs) =>
        inputs.Select(i => new LoadBalancerListenerSpec(Enum.Parse<LoadBalancerProtocol>(i.Protocol), i.Port, i.TargetPort))
            .ToList();

    /// A VM that does not exist and a VM of another tenant answer the same, so neither tells the caller about the
    /// other's resources (titan-ops architecture/http-contract.md#tenant-scoping). The aggregate checks the rest.
    public static async Task<List<VmAggregate>> BackendsAsync(
        IVmRepository vmRepo, Guid tenantId, List<LoadBalancerBackendInput> inputs, CancellationToken ct)
    {
        List<Guid> ids = inputs.Select(b => b.VmId).ToList();
        List<VmAggregate> found = await vmRepo.ListByIdsAsync(ids, ct);
        var backends = new List<VmAggregate>();
        foreach (Guid id in ids)
        {
            VmAggregate? vm = found.FirstOrDefault(v => v.Id == id);
            bool isTheCallersVm = vm != null && vm.TenantId == tenantId;
            if (!isTheCallersVm)
            {
                throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID", $"VM '{id}' is not one of this tenant's VMs.");
            }
            backends.Add(vm!);
        }
        return backends;
    }
}
