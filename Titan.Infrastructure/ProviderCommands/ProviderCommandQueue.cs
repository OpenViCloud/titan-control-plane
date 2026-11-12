using System.Globalization;
using System.Text.Json;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmOfferingAggregate = Titan.Domain.Aggregates.VmOffering.VmOffering;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.ProviderCommands;

/// Stages provider commands in the scope's DbContext (titan-ops architecture/provider-and-worker.md#provider-command-table).
/// Keys are the former workflow ids, so the repeat rules of titan-ops architecture/intent-and-observation.md#intent-has-identity
/// keep their shape, except those of the resources on the request lifecycle, which carry the request id
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request); payload property names are what the worker reads,
/// and only those: the cluster goes on the row, which the worker reads from the lease.
public class ProviderCommandQueue(IProviderCommandRepository repository, TimeProvider clock) : IProviderCommandQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Network and firewall rule: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueNetworkCreateAsync(NetworkAggregate network, Guid requestId, string projectName,
        string? publicIpv6RouterAddress, CancellationToken ct = default) =>
        EnqueueAsync(NetworkCreateKey(network.Id, requestId), ProviderCommandKinds.NetworkCreate, network.ClusterId, network.TenantId,
            new
            {
                networkId = network.Id, requestId, name = network.Name.Value, projectName,
                cidr = network.ChosenCidr?.Value ?? "", defaultIngressPolicy = network.DefaultIngressPolicy.ToString(),
                defaultEgressPolicy = network.DefaultEgressPolicy.ToString(), publicIpv6Subnet = network.PublicIpv6Subnet ?? "",
                publicIpv6RouterAddress = publicIpv6RouterAddress ?? ""
            }, ct);

    public Task EnqueueNetworkDeleteAsync(NetworkAggregate network, Guid requestId, string externalNetworkId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(NetworkDeleteKey(network.Id, requestId), ProviderCommandKinds.NetworkDelete, network.ClusterId, network.TenantId,
            new { networkId = network.Id, requestId, externalNetworkId, projectName }, ct);

    // The worker builds the ACL entry from these (OpenViCloud/titan-incus-worker:resource/firewall/rule.go AclRule), with
    // direction and protocol in its lower-case spelling, and names the ACL from networkId (resource/firewall/create.go).
    public Task EnqueueFirewallRuleCreateAsync(NetworkAggregate network, FirewallRule rule, Guid requestId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(FirewallRuleCreateKey(rule.Id, requestId), ProviderCommandKinds.FirewallRuleCreate, network.ClusterId, network.TenantId,
            new
            {
                ruleId = rule.Id, requestId, networkId = network.Id, direction = rule.Direction.ToString().ToLowerInvariant(),
                protocol = rule.Protocol.ToString().ToLowerInvariant(), startPort = rule.StartPort, endPort = rule.EndPort,
                cidr = rule.Cidr.Value, projectName
            }, ct);

    // networkId: the worker needs it for the callback URL that reports the delete (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    public Task EnqueueFirewallRuleDeleteAsync(NetworkAggregate network, FirewallRule rule, Guid requestId,
        string externalRuleId, string projectName, CancellationToken ct = default) =>
        EnqueueAsync(FirewallRuleDeleteKey(rule.Id, requestId), ProviderCommandKinds.FirewallRuleDelete, network.ClusterId, network.TenantId,
            new { ruleId = rule.Id, requestId, networkId = network.Id, externalRuleId, projectName }, ct);

    // `network-{kind}-{networkId}-{requestId}` and `firewall-rule-{kind}-{ruleId}-{requestId}`: the stuck-request query
    // rebuilds the key from the waiting request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string NetworkCreateKey(Guid networkId, Guid requestId) => $"network-create-{networkId}-{requestId}";
    internal static string NetworkDeleteKey(Guid networkId, Guid requestId) => $"network-delete-{networkId}-{requestId}";
    internal static string FirewallRuleCreateKey(Guid ruleId, Guid requestId) => $"firewall-rule-create-{ruleId}-{requestId}";
    internal static string FirewallRuleDeleteKey(Guid ruleId, Guid requestId) => $"firewall-rule-delete-{ruleId}-{requestId}";

    // VM: one row per request, the key carries the request id, so no key is ever reused
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request). The payload carries requestId for the
    // worker's report.
    public Task EnqueueVmCreateAsync(VmAggregate vm, Guid requestId, VmOfferingAggregate offering, string externalTemplateId,
        string externalNetworkId, string projectName, string? vendorData, CancellationToken ct = default) =>
        EnqueueAsync(VmCreateKey(vm.Id, requestId), ProviderCommandKinds.VmCreate, vm.ClusterId, vm.TenantId,
            new
            {
                vmId = vm.Id, requestId, name = vm.Name, vcpuCount = offering.VcpuCount,
                memoryMb = offering.MemoryMb, templateId = externalTemplateId, externalNetworkId,
                projectName, diskSizeGb = vm.DiskSizeGb, userData = vm.UserData ?? "", vendorData = vendorData ?? ""
            }, ct);

    public Task EnqueueVmDeleteAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(VmDeleteKey(vm.Id, requestId), ProviderCommandKinds.VmDelete, vm.ClusterId, vm.TenantId,
            new { vmId = vm.Id, requestId, externalVmId, projectName }, ct);

    public Task EnqueueVmStopAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(VmStopKey(vm.Id, requestId), ProviderCommandKinds.VmStop, vm.ClusterId, vm.TenantId,
            new { vmId = vm.Id, requestId, externalVmId, projectName }, ct);

    public Task EnqueueVmStartAsync(VmAggregate vm, Guid requestId, string externalVmId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(VmStartKey(vm.Id, requestId), ProviderCommandKinds.VmStart, vm.ClusterId, vm.TenantId,
            new { vmId = vm.Id, requestId, externalVmId, projectName }, ct);

    // `vm-{kind}-{vmId}-{requestId}`: the stuck-request query rebuilds the key from the VM's waiting request
    // (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string VmCreateKey(Guid vmId, Guid requestId) => $"vm-create-{vmId}-{requestId}";
    internal static string VmDeleteKey(Guid vmId, Guid requestId) => $"vm-delete-{vmId}-{requestId}";
    internal static string VmStopKey(Guid vmId, Guid requestId) => $"vm-stop-{vmId}-{requestId}";
    internal static string VmStartKey(Guid vmId, Guid requestId) => $"vm-start-{vmId}-{requestId}";

    // VM snapshot: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueVmSnapshotCreateAsync(VmAggregate vm, VmSnapshot snapshot, Guid requestId, string externalVmId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VmSnapshotCreateKey(snapshot.Id, requestId), ProviderCommandKinds.VmSnapshotCreate, vm.ClusterId, vm.TenantId,
            new { snapshotId = snapshot.Id, requestId, vmId = vm.Id, externalVmId, projectName }, ct);

    public Task EnqueueVmSnapshotRevertAsync(VmAggregate vm, VmSnapshot snapshot, Guid requestId, string externalSnapshotId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VmSnapshotRevertKey(snapshot.Id, requestId), ProviderCommandKinds.VmSnapshotRevert, vm.ClusterId, vm.TenantId,
            new { snapshotId = snapshot.Id, requestId, vmId = vm.Id, externalSnapshotId, projectName }, ct);

    public Task EnqueueVmSnapshotDeleteAsync(VmAggregate vm, VmSnapshot snapshot, Guid requestId, string externalSnapshotId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VmSnapshotDeleteKey(snapshot.Id, requestId), ProviderCommandKinds.VmSnapshotDelete, vm.ClusterId, vm.TenantId,
            new { snapshotId = snapshot.Id, requestId, vmId = vm.Id, externalSnapshotId, projectName }, ct);

    // `vm-snapshot-{kind}-{snapshotId}-{requestId}`: the stuck-request query rebuilds the key from the snapshot's waiting
    // request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string VmSnapshotCreateKey(Guid snapshotId, Guid requestId) => $"vm-snapshot-create-{snapshotId}-{requestId}";
    internal static string VmSnapshotRevertKey(Guid snapshotId, Guid requestId) => $"vm-snapshot-revert-{snapshotId}-{requestId}";
    internal static string VmSnapshotDeleteKey(Guid snapshotId, Guid requestId) => $"vm-snapshot-delete-{snapshotId}-{requestId}";

    // Volume and volume snapshot: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    // A restore is the new volume's create, so its row is keyed like one and the stuck-request query finds it.
    public Task EnqueueVolumeCreateAsync(VolumeAggregate volume, Guid requestId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(VolumeCreateKey(volume.Id, requestId), ProviderCommandKinds.VolumeCreate, volume.ClusterId, volume.TenantId,
            new { volumeId = volume.Id, requestId, name = volume.Name, sizeGb = volume.SizeGb, projectName }, ct);

    public Task EnqueueVolumeAttachAsync(VolumeAggregate volume, Guid requestId, string externalVolumeId, string externalVmId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VolumeAttachKey(volume.Id, requestId), ProviderCommandKinds.VolumeAttach, volume.ClusterId, volume.TenantId,
            new { volumeId = volume.Id, requestId, externalVolumeId, externalVmId, projectName }, ct);

    public Task EnqueueVolumeDetachAsync(VolumeAggregate volume, Guid requestId, string externalVolumeId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(VolumeDetachKey(volume.Id, requestId), ProviderCommandKinds.VolumeDetach, volume.ClusterId, volume.TenantId,
            new { volumeId = volume.Id, requestId, externalVolumeId, projectName }, ct);

    public Task EnqueueVolumeDeleteAsync(VolumeAggregate volume, Guid requestId, string externalVolumeId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(VolumeDeleteKey(volume.Id, requestId), ProviderCommandKinds.VolumeDelete, volume.ClusterId, volume.TenantId,
            new { volumeId = volume.Id, requestId, externalVolumeId, projectName }, ct);

    public Task EnqueueVolumeSnapshotCreateAsync(VolumeAggregate volume, VolumeSnapshot snapshot, Guid requestId,
        string externalVolumeId, string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VolumeSnapshotCreateKey(snapshot.Id, requestId), ProviderCommandKinds.VolumeSnapshotCreate, volume.ClusterId, volume.TenantId,
            new { snapshotId = snapshot.Id, requestId, volumeId = volume.Id, externalVolumeId, projectName }, ct);

    public Task EnqueueVolumeSnapshotDeleteAsync(VolumeAggregate volume, VolumeSnapshot snapshot, Guid requestId,
        string externalSnapshotId, string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VolumeSnapshotDeleteKey(snapshot.Id, requestId), ProviderCommandKinds.VolumeSnapshotDelete, volume.ClusterId, volume.TenantId,
            new { snapshotId = snapshot.Id, requestId, volumeId = volume.Id, externalSnapshotId, projectName }, ct);

    // The new volume takes the source's cluster and its own name (titan-ops features/volume/design.md#volume-snapshot).
    public Task EnqueueVolumeSnapshotRestoreAsync(VolumeAggregate newVolume, Guid requestId, string externalSnapshotId,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VolumeCreateKey(newVolume.Id, requestId), ProviderCommandKinds.VolumeSnapshotRestore, newVolume.ClusterId, newVolume.TenantId,
            new { newVolumeId = newVolume.Id, requestId, externalSnapshotId, name = newVolume.Name, projectName }, ct);

    // `volume-{kind}-{volumeId}-{requestId}` and `volume-snapshot-{kind}-{snapshotId}-{requestId}`: the stuck-request
    // query rebuilds the key from the waiting request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string VolumeCreateKey(Guid volumeId, Guid requestId) => $"volume-create-{volumeId}-{requestId}";
    internal static string VolumeAttachKey(Guid volumeId, Guid requestId) => $"volume-attach-{volumeId}-{requestId}";
    internal static string VolumeDetachKey(Guid volumeId, Guid requestId) => $"volume-detach-{volumeId}-{requestId}";
    internal static string VolumeDeleteKey(Guid volumeId, Guid requestId) => $"volume-delete-{volumeId}-{requestId}";
    internal static string VolumeSnapshotCreateKey(Guid snapshotId, Guid requestId) => $"volume-snapshot-create-{snapshotId}-{requestId}";
    internal static string VolumeSnapshotDeleteKey(Guid snapshotId, Guid requestId) => $"volume-snapshot-delete-{snapshotId}-{requestId}";

    // The schedule asks every hour under the day's key: the day's backup that ran stays done, one that failed runs again.
    public Task EnqueueVmBackupAsync(VmAggregate vm, string externalVmId, string projectName, DateOnly day,
        CancellationToken ct = default) =>
        EnqueueAsync($"vm-backup-{vm.Id}-{BackupDay(day)}", ProviderCommandKinds.VmBackup, vm.ClusterId, vm.TenantId,
            new { vmId = vm.Id, externalVmId, projectName, tenantId = vm.TenantId }, ct);

    public Task EnqueueVolumeBackupAsync(VolumeAggregate volume, string externalVolumeId, string projectName, DateOnly day,
        CancellationToken ct = default) =>
        EnqueueAsync($"volume-backup-{volume.Id}-{BackupDay(day)}", ProviderCommandKinds.VolumeBackup, volume.ClusterId, volume.TenantId,
            new { volumeId = volume.Id, externalVolumeId, projectName, tenantId = volume.TenantId }, ct);

    // Gregorian digits whatever the host's culture: a Buddhist-calendar culture would write another year into the key.
    private static string BackupDay(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // Load balancer: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueLoadBalancerCreateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(LoadBalancerCreateKey(loadBalancer.Id, requestId), ProviderCommandKinds.LoadBalancerCreate,
            loadBalancer.ClusterId, loadBalancer.TenantId, LoadBalancerPayload(loadBalancer, requestId, projectName), ct);

    public Task EnqueueLoadBalancerUpdateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(LoadBalancerUpdateKey(loadBalancer.Id, requestId), ProviderCommandKinds.LoadBalancerUpdate,
            loadBalancer.ClusterId, loadBalancer.TenantId, LoadBalancerPayload(loadBalancer, requestId, projectName), ct);

    public Task EnqueueLoadBalancerDeleteAsync(LoadBalancer loadBalancer, Guid requestId, string vip, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(LoadBalancerDeleteKey(loadBalancer.Id, requestId), ProviderCommandKinds.LoadBalancerDelete, loadBalancer.ClusterId, loadBalancer.TenantId,
            new { loadBalancerId = loadBalancer.Id, requestId, externalNetworkId = loadBalancer.ExternalNetworkId, vip, projectName }, ct);

    // `load-balancer-{kind}-{loadBalancerId}-{requestId}`: the stuck-request query rebuilds the key from the load
    // balancer's waiting request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string LoadBalancerCreateKey(Guid loadBalancerId, Guid requestId) => $"load-balancer-create-{loadBalancerId}-{requestId}";
    internal static string LoadBalancerUpdateKey(Guid loadBalancerId, Guid requestId) => $"load-balancer-update-{loadBalancerId}-{requestId}";
    internal static string LoadBalancerDeleteKey(Guid loadBalancerId, Guid requestId) => $"load-balancer-delete-{loadBalancerId}-{requestId}";

    // The asked configuration (titan-ops features/load-balancer/design.md#lb-resource-model); protocol in Incus's spelling.
    private static object LoadBalancerPayload(LoadBalancer lb, Guid requestId, string projectName) =>
        new
        {
            loadBalancerId = lb.Id,
            requestId,
            projectName,
            externalNetworkId = lb.ExternalNetworkId,
            publicIpv6Subnet = lb.NetworkSubnet,
            vip = lb.Vip,
            listeners = lb.DesiredListeners
                .OrderBy(l => l.Protocol).ThenBy(l => l.Port)
                .Select(l => new { protocol = l.Protocol.ToString().ToLowerInvariant(), port = l.Port, targetPort = l.TargetPort })
                .ToList(),
            backends = lb.DesiredBackends
                .OrderBy(b => b.ExternalVmId, StringComparer.Ordinal)
                .Select(b => new { externalVmId = b.ExternalVmId })
                .ToList()
        };

    // One row per sync request of one cluster (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueTemplateSyncAsync(VmTemplateAggregate template, string clusterId, Guid requestId,
        CancellationToken ct = default) =>
        EnqueueAsync(TemplateSyncKey(template.Id, clusterId, requestId), ProviderCommandKinds.TemplateSync, clusterId,
            tenantId: null,
            new { templateId = template.Id, requestId, name = template.Name, osImageName = template.OsImageName, imageUrl = template.ImageUrl ?? "" }, ct);

    // `template-sync-{templateId}-{clusterId}-{requestId}`: the stuck-request query rebuilds it from the cluster's
    // waiting sync (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string TemplateSyncKey(Guid templateId, string clusterId, Guid requestId) =>
        $"template-sync-{templateId}-{clusterId}-{requestId}";

    private async Task EnqueueAsync(string key, string kind, string clusterId, Guid? tenantId, object payload,
        CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, Json);
        DateTimeOffset now = clock.GetUtcNow();
        ProviderCommand? existing = await repository.FindByKeyAsync(key, ct);
        if (existing == null)
        {
            repository.Add(ProviderCommand.Create(key, kind, clusterId, json, now, tenantId));
            return;
        }
        // Absorbed or reopened (ProviderCommand.Reopen); either way the handler's SaveAsync writes the outcome.
        existing.Reopen(json, now);
    }
}
