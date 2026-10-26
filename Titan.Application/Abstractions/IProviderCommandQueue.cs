using Titan.Domain.Aggregates.Compute.LoadBalancer;

namespace Titan.Application.Abstractions;

/// Enqueues work for the provider worker (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table). Each call
/// stages a row in the scope's DbContext: it is written by the handler's own SaveAsync, in the same transaction as
/// the aggregate change, so call it BEFORE saving. Nothing reaches the worker until that commit.
/// A repeat of the same key follows the row's rerun rule (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-key).
public interface IProviderCommandQueue
{
    // publicIpv6Subnet/publicIpv6RouterAddress: both null when the network has no public IPv6
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/infra/incus/design.md#ipv6-address-plan).
    Task EnqueueNetworkCreateAsync(Guid networkId, string name, string zoneId, string projectName, string? cidr, string defaultIngressPolicy, string defaultEgressPolicy, string? publicIpv6Subnet, string? publicIpv6RouterAddress, CancellationToken ct = default);
    Task EnqueueNetworkDeleteAsync(Guid networkId, string externalNetworkId, string projectName, string zoneId, CancellationToken ct = default);

    Task EnqueueFirewallRuleCreateAsync(Guid ruleId, Guid networkId, string direction, string protocol, int? startPort, int? endPort, string cidr, string externalNetworkId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueFirewallRuleDeleteAsync(Guid ruleId, Guid networkId, string externalRuleId, string projectName, string zoneId, CancellationToken ct = default);

    // cpuUnits/vcpuCount/memoryMb come from the selected VM offering (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#vm-offering-sizing): the
    // worker has no access to Titan's catalog, so the resolved sizing travels with the command. vendorData is Titan's own
    // cloud-init script (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-key), kept apart from the customer's userData.
    Task EnqueueVmCreateAsync(Guid vmId, string name, string vmOfferingId,
        decimal cpuUnits, int vcpuCount, int memoryMb, string templateId, string zoneId,
        string externalNetworkId, string projectName, int diskSizeGb,
        string? userData, string? vendorData, CancellationToken ct = default);
    Task EnqueueVmDeleteAsync(Guid vmId, string externalVmId, string projectName, string zoneId, CancellationToken ct = default);
    Task ResendVmDeleteAsync(Guid vmId, string externalVmId, string projectName, string zoneId, CancellationToken ct = default);
    // Stop/start only actuate the provider; the reconciler observes the result. intentId is Vm.PendingIntentId
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity): a repeat enqueues the same key.
    Task EnqueueVmStopAsync(Guid vmId, Guid intentId, string externalVmId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVmStartAsync(Guid vmId, Guid intentId, string externalVmId, string projectName, string zoneId, CancellationToken ct = default);

    // zoneId is the VM's: a snapshot lives on the VM's cluster. projectName is the tenant's Incus project, where the
    // instance, its snapshots and the custom volumes live (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#project-name).
    Task EnqueueVmSnapshotCreateAsync(Guid snapshotId, Guid vmId, string externalVmId, string name, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVmSnapshotRevertAsync(Guid snapshotId, Guid vmId, string externalSnapshotId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVmSnapshotDeleteAsync(Guid snapshotId, Guid vmId, string externalSnapshotId, string projectName, string zoneId, CancellationToken ct = default);

    // No offering id: the worker has no catalog, the pool comes from the zone's cluster and the size travels here.
    Task EnqueueVolumeCreateAsync(Guid volumeId, string name, string zoneId, int sizeGb,
        string projectName, CancellationToken ct = default);
    Task EnqueueVolumeAttachAsync(Guid volumeId, string externalVolumeId, string externalVmId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVolumeDetachAsync(Guid volumeId, string externalVolumeId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVolumeDeleteAsync(Guid volumeId, string externalVolumeId, string projectName, string zoneId, CancellationToken ct = default);

    Task EnqueueVolumeSnapshotCreateAsync(Guid snapshotId, Guid volumeId, string externalVolumeId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVolumeSnapshotDeleteAsync(Guid snapshotId, Guid volumeId, string externalSnapshotId, string projectName, string zoneId, CancellationToken ct = default);
    Task EnqueueVolumeSnapshotRestoreAsync(Guid snapshotId, Guid newVolumeId, string externalSnapshotId, string name, string projectName, string zoneId, CancellationToken ct = default);

    // One a day per resource, the UTC day in the key (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#incus-backup).
    Task EnqueueVmBackupAsync(Guid vmId, string externalVmId, string projectName, Guid tenantId, string zoneId, DateOnly day, CancellationToken ct = default);
    Task EnqueueVolumeBackupAsync(Guid volumeId, string externalVolumeId, string projectName, Guid tenantId, string zoneId, DateOnly day, CancellationToken ct = default);

    // The whole configuration travels each time: the worker writes it to Incus in one PUT
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-resource-model). The update is keyed on its intent.
    Task EnqueueLoadBalancerCreateAsync(LoadBalancer loadBalancer, string projectName, CancellationToken ct = default);
    Task EnqueueLoadBalancerUpdateAsync(LoadBalancer loadBalancer, Guid intentId, string projectName, CancellationToken ct = default);
    Task EnqueueLoadBalancerDeleteAsync(Guid loadBalancerId, string externalNetworkId, string vip, string projectName, string zoneId, CancellationToken ct = default);

    Task EnqueueTemplateSyncAsync(Guid templateId, string zoneId, string name, string? description, string osImageName,
        string? imageUrl, int? rootDiskSizeGb, CancellationToken ct = default);
}
