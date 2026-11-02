using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

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
        // Each row locked, so a delete of one of them waits for this request and then sees it as a backend
        // (titan-ops architecture/domain-services.md#cross-aggregate-lock). In one fixed order (.NET Guid order), so two
        // requests naming the same VMs cannot each hold one the other waits for; at most 20 backends (LoadBalancer.MaxBackends).
        // A VM of another tenant is locked too before it is refused below: the lock ends with this request.
        var found = new List<VmAggregate>();
        foreach (Guid id in ids.Distinct().Order())
        {
            VmAggregate? vm = await vmRepo.FindByIdForUpdateAsync(id, ct);
            if (vm != null)
            {
                found.Add(vm);
            }
        }
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
