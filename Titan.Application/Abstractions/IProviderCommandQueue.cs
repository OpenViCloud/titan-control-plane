using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmOfferingAggregate = Titan.Domain.Aggregates.VmOffering.VmOffering;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Abstractions;

/// Enqueues work for the provider worker (titan-ops architecture/provider-and-worker.md#provider-command-table). Each call
/// stages a row in the scope's DbContext: it is written by the handler's own SaveAsync, in the same transaction as
/// the aggregate change, so call it BEFORE saving. Nothing reaches the worker until that commit.
/// A repeat of a key that has a row is absorbed while the row is open or Done, and reopens a Failed one
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
/// The cluster travels on the row, not in the payload: the worker reads it from the lease
/// (titan-ops architecture/provider-and-worker.md#provider-command-lease).
public interface IProviderCommandQueue
{
    // publicIpv6RouterAddress: null when the network has no public IPv6 (titan-ops infra/incus/design.md#ipv6-address-plan);
    // the route that gives it commits with the network, so it is handed in rather than read from the aggregate.
    Task EnqueueNetworkCreateAsync(NetworkAggregate network, Guid requestId, string projectName, string? publicIpv6RouterAddress, CancellationToken ct = default);
    Task EnqueueNetworkDeleteAsync(NetworkAggregate network, Guid requestId, string externalNetworkId, string projectName, CancellationToken ct = default);

    Task EnqueueFirewallRuleCreateAsync(NetworkAggregate network, FirewallRule rule, Guid requestId, string projectName, CancellationToken ct = default);
    Task EnqueueFirewallRuleDeleteAsync(NetworkAggregate network, FirewallRule rule, Guid requestId, string externalRuleId, string projectName, CancellationToken ct = default);

    // vcpuCount/memoryMb from the VM offering the customer picked (titan-ops features/catalog/design.md#vm-offering-sizing):
    // the worker has no access to Titan's catalog, so the resolved sizing travels and the offering id does not
    // (titan-ops features/catalog/design.md#catalog-resolution-at-create). vendorData is Titan's own cloud-init script
    // (titan-ops features/vm/design.md#ssh-key), kept apart from the customer's userData.
    Task EnqueueVmCreateAsync(VmAggregate vm, Guid requestId, VmOfferingAggregate offering, string externalTemplateId,
        string externalNetworkId, string projectName, string? vendorData, CancellationToken ct = default);
    Task EnqueueVmDeleteAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);
    Task EnqueueVmStopAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);
    Task EnqueueVmStartAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);

    // A snapshot lives on its VM's cluster, in the tenant's Incus project with the instance
    // (titan-ops architecture/tenancy-and-admin.md#project-name).
    Task EnqueueVmSnapshotCreateAsync(VmAggregate vm, VmSnapshot snapshot, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);
    Task EnqueueVmSnapshotRevertAsync(VmAggregate vm, VmSnapshot snapshot, Guid requestId, string externalSnapshotId, string projectName, CancellationToken ct = default);
    Task EnqueueVmSnapshotDeleteAsync(VmAggregate vm, VmSnapshot snapshot, Guid requestId, string externalSnapshotId, string projectName, CancellationToken ct = default);

    // No offering id: the worker has no catalog, the pool comes from the cluster and the size travels here.
    Task EnqueueVolumeCreateAsync(VolumeAggregate volume, Guid requestId, string projectName, CancellationToken ct = default);
    Task EnqueueVolumeAttachAsync(VolumeAggregate volume, Guid requestId, string externalVolumeId, string externalVmId, string projectName, CancellationToken ct = default);
    Task EnqueueVolumeDetachAsync(VolumeAggregate volume, Guid requestId, string externalVolumeId, string projectName, CancellationToken ct = default);
    Task EnqueueVolumeDeleteAsync(VolumeAggregate volume, Guid requestId, string externalVolumeId, string projectName, CancellationToken ct = default);

    Task EnqueueVolumeSnapshotCreateAsync(VolumeAggregate volume, VolumeSnapshot snapshot, Guid requestId, string externalVolumeId, string projectName, CancellationToken ct = default);
    Task EnqueueVolumeSnapshotDeleteAsync(VolumeAggregate volume, VolumeSnapshot snapshot, Guid requestId, string externalSnapshotId, string projectName, CancellationToken ct = default);
    // The restore's request is the new volume's create.
    Task EnqueueVolumeSnapshotRestoreAsync(VolumeAggregate newVolume, Guid requestId, string externalSnapshotId, string projectName, CancellationToken ct = default);

    // One a day per resource, the UTC day in the key (titan-ops features/backup/design.md#incus-backup).
    Task EnqueueVmBackupAsync(VmAggregate vm, string externalVmId, string projectName, DateOnly day, CancellationToken ct = default);
    Task EnqueueVolumeBackupAsync(VolumeAggregate volume, string externalVolumeId, string projectName, DateOnly day, CancellationToken ct = default);

    // The whole configuration travels each time: the worker writes it to Incus in one PUT
    // (titan-ops features/load-balancer/design.md#lb-resource-model).
    Task EnqueueLoadBalancerCreateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName, CancellationToken ct = default);
    Task EnqueueLoadBalancerUpdateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName, CancellationToken ct = default);
    Task EnqueueLoadBalancerDeleteAsync(LoadBalancer loadBalancer, Guid requestId, string vip, string projectName, CancellationToken ct = default);

    // One command per sync request of one cluster.
    Task EnqueueTemplateSyncAsync(VmTemplateAggregate template, string clusterId, Guid requestId, CancellationToken ct = default);
}
