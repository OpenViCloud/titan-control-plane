using Titan.Domain.Aggregates.LoadBalancer;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmOfferingAggregate = Titan.Domain.Aggregates.VmOffering.VmOffering;

namespace Titan.Application.Abstractions;

/// Enqueues work for the provider worker (titan-ops architecture/provider-and-worker.md#provider-command-table). Each call
/// stages a row in the scope's DbContext: it is written by the handler's own SaveAsync, in the same transaction as
/// the aggregate change, so call it BEFORE saving. Nothing reaches the worker until that commit.
/// A repeat of a key that has a row is absorbed while the row is open or Done, and reopens a Failed one
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
public interface IProviderCommandQueue
{
    // publicIpv6Subnet/publicIpv6RouterAddress: both null when the network has no public IPv6
    // (titan-ops infra/incus/design.md#ipv6-address-plan).
    Task EnqueueNetworkCreateAsync(Guid networkId, Guid requestId, string name, string clusterId, string projectName, string? cidr, string defaultIngressPolicy, string defaultEgressPolicy, string? publicIpv6Subnet, string? publicIpv6RouterAddress, CancellationToken ct = default);
    Task EnqueueNetworkDeleteAsync(Guid networkId, Guid requestId, string externalNetworkId, string projectName, string clusterId, CancellationToken ct = default);

    Task EnqueueFirewallRuleCreateAsync(Guid ruleId, Guid requestId, Guid networkId, string direction, string protocol, int? startPort, int? endPort, string cidr, string externalNetworkId, string projectName, string clusterId, CancellationToken ct = default);
    Task EnqueueFirewallRuleDeleteAsync(Guid ruleId, Guid requestId, Guid networkId, string externalRuleId, string projectName, string clusterId, CancellationToken ct = default);

    // The VM's own fields (id, name, cluster, disk size, user-data) travel from the aggregate; cpuUnits/vcpuCount/memoryMb
    // from the VM offering the customer picked (titan-ops features/catalog/design.md#vm-offering-sizing): the worker has no
    // access to Titan's catalog, so the resolved sizing travels and the offering id does not
    // (titan-ops features/catalog/design.md#catalog-resolution-at-create). vendorData is Titan's own cloud-init script
    // (titan-ops features/vm/design.md#ssh-key), kept apart from the customer's userData. requestId is the VM's waiting
    // request (Vm.Operation.PendingId): one command per request, the key and the payload carry the id, and the worker's
    // report brings it back (titan-ops architecture/resource-lifecycle.md#one-key-per-request). externalVmId is the
    // instance name the aggregate handed out with the move
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    Task EnqueueVmCreateAsync(VmAggregate vm, Guid requestId, VmOfferingAggregate offering, string externalTemplateId,
        string externalNetworkId, string projectName, string? vendorData, CancellationToken ct = default);
    Task EnqueueVmDeleteAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);
    Task EnqueueVmStopAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);
    Task EnqueueVmStartAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName, CancellationToken ct = default);

    // clusterId is the VM's: a snapshot lives on the VM's cluster. projectName is the tenant's Incus project, where the
    // instance, its snapshots and the custom volumes live (titan-ops architecture/tenancy-and-admin.md#project-name).
    Task EnqueueVmSnapshotCreateAsync(Guid snapshotId, Guid requestId, Guid vmId, string externalVmId, string name, string projectName, string clusterId, CancellationToken ct = default);
    Task EnqueueVmSnapshotRevertAsync(Guid snapshotId, Guid requestId, Guid vmId, string externalSnapshotId, string projectName, string clusterId, CancellationToken ct = default);
    Task EnqueueVmSnapshotDeleteAsync(Guid snapshotId, Guid requestId, Guid vmId, string externalSnapshotId, string projectName, string clusterId, CancellationToken ct = default);

    // No offering id: the worker has no catalog, the pool comes from the cluster and the size travels here. Every
    // volume and volume snapshot command carries the request it was enqueued for
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    Task EnqueueVolumeCreateAsync(Guid volumeId, Guid requestId, string name, string clusterId, int sizeGb,
        string projectName, CancellationToken ct = default);
    Task EnqueueVolumeAttachAsync(Guid volumeId, Guid requestId, string externalVolumeId, string externalVmId, string projectName, string clusterId, CancellationToken ct = default);
    Task EnqueueVolumeDetachAsync(Guid volumeId, Guid requestId, string externalVolumeId, string projectName, string clusterId, CancellationToken ct = default);
    Task EnqueueVolumeDeleteAsync(Guid volumeId, Guid requestId, string externalVolumeId, string projectName, string clusterId, CancellationToken ct = default);

    Task EnqueueVolumeSnapshotCreateAsync(Guid snapshotId, Guid requestId, Guid volumeId, string externalVolumeId, string projectName, string clusterId, CancellationToken ct = default);
    Task EnqueueVolumeSnapshotDeleteAsync(Guid snapshotId, Guid requestId, Guid volumeId, string externalSnapshotId, string projectName, string clusterId, CancellationToken ct = default);
    // The restore's request is the new volume's create.
    Task EnqueueVolumeSnapshotRestoreAsync(Guid snapshotId, Guid newVolumeId, Guid requestId, string externalSnapshotId, string name, string projectName, string clusterId, CancellationToken ct = default);

    // One a day per resource, the UTC day in the key (titan-ops features/backup/design.md#incus-backup).
    Task EnqueueVmBackupAsync(Guid vmId, string externalVmId, string projectName, Guid tenantId, string clusterId, DateOnly day, CancellationToken ct = default);
    Task EnqueueVolumeBackupAsync(Guid volumeId, string externalVolumeId, string projectName, Guid tenantId, string clusterId, DateOnly day, CancellationToken ct = default);

    // The whole configuration travels each time: the worker writes it to Incus in one PUT
    // (titan-ops features/load-balancer/design.md#lb-resource-model). Every command carries the request it was enqueued for.
    Task EnqueueLoadBalancerCreateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName, CancellationToken ct = default);
    Task EnqueueLoadBalancerUpdateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName, CancellationToken ct = default);
    Task EnqueueLoadBalancerDeleteAsync(Guid loadBalancerId, Guid requestId, string externalNetworkId, string vip, string projectName, string clusterId, CancellationToken ct = default);

    // One command per sync request of one cluster; it carries the request it was enqueued for.
    Task EnqueueTemplateSyncAsync(Guid templateId, string clusterId, Guid requestId, string name, string? description,
        string osImageName, string? imageUrl, int? rootDiskSizeGb, CancellationToken ct = default);
}
