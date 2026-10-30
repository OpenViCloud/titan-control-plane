using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Compute;

/// <summary>
/// The one place Compute decides whether a caller may see a resource.
///
/// Every lookup here answers <b>404</b> when the resource belongs to another tenant — never 403, which
/// would tell a caller about resources they cannot see (titan-ops architecture/http-contract.md#tenant-scoping).
/// A missing resource and a foreign one are indistinguishable, down to the error code and the message.
/// ComputeOwnershipTests pins it. A VM's or a volume's snapshot is reached through its parent
/// (Vm.RequireSnapshot, Volume.RequireSnapshot): a child entity, not a lookup of its own.
/// </summary>
public static class ComputeOwnership
{
    public static async Task<VmAggregate> RequireVmAsync(
        IVmRepository vmRepo, Guid tenantId, Guid vmId, CancellationToken ct)
    {
        var vm = await vmRepo.FindByIdAsync(vmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        if (vm.TenantId != tenantId)
            throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        return vm;
    }

    /// The same answer with the VM's row locked (IVmRepository.FindByIdForUpdateAsync): for a request that adds to the
    /// VM and must count what it already holds (titan-ops architecture/code-structure.md#child-entity).
    public static async Task<VmAggregate> RequireVmForUpdateAsync(
        IVmRepository vmRepo, Guid tenantId, Guid vmId, CancellationToken ct)
    {
        var vm = await vmRepo.FindByIdForUpdateAsync(vmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        if (vm.TenantId != tenantId)
            throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        return vm;
    }

    public static async Task<LoadBalancerAggregate> RequireLoadBalancerAsync(
        ILoadBalancerRepository repo, Guid tenantId, Guid loadBalancerId, CancellationToken ct)
    {
        var loadBalancer = await repo.FindByIdAsync(loadBalancerId, ct)
            ?? throw new NotFoundException("LOAD_BALANCER_NOT_FOUND", "Load balancer not found.");

        if (loadBalancer.TenantId != tenantId)
            throw new NotFoundException("LOAD_BALANCER_NOT_FOUND", "Load balancer not found.");

        return loadBalancer;
    }

    public static async Task<Volume> RequireVolumeAsync(
        IVolumeRepository volumeRepo, Guid tenantId, Guid volumeId, CancellationToken ct)
    {
        var volume = await volumeRepo.FindByIdAsync(volumeId, ct)
            ?? throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        if (volume.TenantId != tenantId)
            throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");

        return volume;
    }
}
