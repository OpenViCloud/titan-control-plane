using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using Titan.Domain.Aggregates.VmSnapshot;
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
/// ComputeOwnershipTests pins it.
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

    public static async Task<VmSnapshot> RequireVmSnapshotAsync(
        IVmSnapshotRepository snapshotRepo, Guid snapshotId, Guid vmId, CancellationToken ct)
    {
        var snapshot = await snapshotRepo.FindByIdAsync(snapshotId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

        if (snapshot.VmId != vmId)
            throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

        return snapshot;
    }
}
