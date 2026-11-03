using System.Globalization;
using System.Text.Json;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints;

namespace Titan.Infrastructure.ProviderCommands;

/// Stages provider commands in the scope's DbContext (titan-ops architecture/provider-and-worker.md#provider-command-table).
/// Keys are the former workflow ids, so the repeat rules of titan-ops architecture/intent-and-observation.md#intent-has-identity
/// keep their shape, except those of the resources on the request lifecycle, which carry the request id
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request); payload property names are what the worker reads.
public class ProviderCommandQueue(IProviderCommandRepository repository, TimeProvider clock) : IProviderCommandQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Network and firewall rule: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueNetworkCreateAsync(Guid networkId, Guid requestId, string name, string clusterId, string projectName,
        string? cidr, string defaultIngressPolicy, string defaultEgressPolicy, string? publicIpv6Subnet,
        string? publicIpv6RouterAddress, CancellationToken ct = default) =>
        EnqueueAsync(NetworkCreateKey(networkId, requestId), ProviderCommandKinds.NetworkCreate, clusterId,
            new
            {
                networkId, requestId, name, projectName, clusterId, cidr = cidr ?? "", defaultIngressPolicy,
                defaultEgressPolicy, publicIpv6Subnet = publicIpv6Subnet ?? "",
                publicIpv6RouterAddress = publicIpv6RouterAddress ?? ""
            },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueNetworkDeleteAsync(Guid networkId, Guid requestId, string externalNetworkId, string projectName,
        string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(NetworkDeleteKey(networkId, requestId), ProviderCommandKinds.NetworkDelete, clusterId,
            new { networkId, requestId, externalNetworkId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueFirewallRuleCreateAsync(Guid ruleId, Guid requestId, Guid networkId, string direction,
        string protocol, int? startPort, int? endPort, string cidr, string externalNetworkId, string projectName,
        string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(FirewallRuleCreateKey(ruleId, requestId), ProviderCommandKinds.FirewallRuleCreate, clusterId,
            new
            {
                ruleId, requestId, networkId, direction, protocol, startPort, endPort, cidr, externalNetworkId,
                projectName, clusterId
            },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // networkId: the worker needs it for the callback URL that reports the delete (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    public Task EnqueueFirewallRuleDeleteAsync(Guid ruleId, Guid requestId, Guid networkId, string externalRuleId,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(FirewallRuleDeleteKey(ruleId, requestId), ProviderCommandKinds.FirewallRuleDelete, clusterId,
            new { ruleId, requestId, networkId, externalRuleId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // `network-{kind}-{networkId}-{requestId}` and `firewall-rule-{kind}-{ruleId}-{requestId}`: the stuck-request query
    // rebuilds the key from the waiting request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string NetworkCreateKey(Guid networkId, Guid requestId) => $"network-create-{networkId}-{requestId}";
    internal static string NetworkDeleteKey(Guid networkId, Guid requestId) => $"network-delete-{networkId}-{requestId}";
    internal static string FirewallRuleCreateKey(Guid ruleId, Guid requestId) => $"firewall-rule-create-{ruleId}-{requestId}";
    internal static string FirewallRuleDeleteKey(Guid ruleId, Guid requestId) => $"firewall-rule-delete-{ruleId}-{requestId}";

    // VM: one row per request, the key carries the request id, so no key is ever reused and the rerun rule never
    // applies (titan-ops architecture/resource-lifecycle.md#one-key-per-request). The payload carries requestId for the
    // worker's report; stop and start also carry it as intentId, which a worker from before the request routes reads
    // for its give-up (titan-ops architecture/resource-lifecycle.md#rollout, removed in phase 6).
    public Task EnqueueVmCreateAsync(Guid vmId, Guid requestId, string name, string vmOfferingId, decimal cpuUnits,
        int vcpuCount, int memoryMb, string templateId, string clusterId, string externalNetworkId, string projectName,
        int diskSizeGb, string? userData, string? vendorData, CancellationToken ct = default) =>
        EnqueueAsync(VmCreateKey(vmId, requestId), ProviderCommandKinds.VmCreate, clusterId,
            new
            {
                vmId, requestId, name, vmOfferingId, cpuUnits, vcpuCount, memoryMb, templateId, clusterId, externalNetworkId,
                projectName, diskSizeGb, userData = userData ?? "", vendorData = vendorData ?? ""
            },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmDeleteAsync(Guid vmId, Guid requestId, string externalVmId, string projectName, string clusterId,
        CancellationToken ct = default) =>
        EnqueueAsync(VmDeleteKey(vmId, requestId), ProviderCommandKinds.VmDelete, clusterId,
            new { vmId, requestId, externalVmId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmStopAsync(Guid vmId, Guid requestId, string externalVmId, string projectName, string clusterId,
        CancellationToken ct = default) =>
        EnqueueAsync(VmStopKey(vmId, requestId), ProviderCommandKinds.VmStop, clusterId,
            new { vmId, requestId, intentId = requestId, externalVmId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmStartAsync(Guid vmId, Guid requestId, string externalVmId, string projectName, string clusterId,
        CancellationToken ct = default) =>
        EnqueueAsync(VmStartKey(vmId, requestId), ProviderCommandKinds.VmStart, clusterId,
            new { vmId, requestId, intentId = requestId, externalVmId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // `vm-{kind}-{vmId}-{requestId}`: the stuck-request query rebuilds the key from the VM's waiting request
    // (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string VmCreateKey(Guid vmId, Guid requestId) => $"vm-create-{vmId}-{requestId}";
    internal static string VmDeleteKey(Guid vmId, Guid requestId) => $"vm-delete-{vmId}-{requestId}";
    internal static string VmStopKey(Guid vmId, Guid requestId) => $"vm-stop-{vmId}-{requestId}";
    internal static string VmStartKey(Guid vmId, Guid requestId) => $"vm-start-{vmId}-{requestId}";

    // VM snapshot: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueVmSnapshotCreateAsync(Guid snapshotId, Guid requestId, Guid vmId, string externalVmId, string name,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VmSnapshotCreateKey(snapshotId, requestId), ProviderCommandKinds.VmSnapshotCreate, clusterId,
            new { snapshotId, requestId, vmId, externalVmId, name, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmSnapshotRevertAsync(Guid snapshotId, Guid requestId, Guid vmId, string externalSnapshotId,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VmSnapshotRevertKey(snapshotId, requestId), ProviderCommandKinds.VmSnapshotRevert, clusterId,
            new { snapshotId, requestId, vmId, externalSnapshotId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmSnapshotDeleteAsync(Guid snapshotId, Guid requestId, Guid vmId, string externalSnapshotId,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VmSnapshotDeleteKey(snapshotId, requestId), ProviderCommandKinds.VmSnapshotDelete, clusterId,
            new { snapshotId, requestId, vmId, externalSnapshotId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // `vm-snapshot-{kind}-{snapshotId}-{requestId}`: the stuck-request query rebuilds the key from the snapshot's waiting
    // request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string VmSnapshotCreateKey(Guid snapshotId, Guid requestId) => $"vm-snapshot-create-{snapshotId}-{requestId}";
    internal static string VmSnapshotRevertKey(Guid snapshotId, Guid requestId) => $"vm-snapshot-revert-{snapshotId}-{requestId}";
    internal static string VmSnapshotDeleteKey(Guid snapshotId, Guid requestId) => $"vm-snapshot-delete-{snapshotId}-{requestId}";

    // Volume and volume snapshot: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    // A restore is the new volume's create, so its row is keyed like one and the stuck-request query finds it.
    public Task EnqueueVolumeCreateAsync(Guid volumeId, Guid requestId, string name, string clusterId, int sizeGb,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync(VolumeCreateKey(volumeId, requestId), ProviderCommandKinds.VolumeCreate, clusterId,
            new { volumeId, requestId, name, clusterId, sizeGb, projectName },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeAttachAsync(Guid volumeId, Guid requestId, string externalVolumeId, string externalVmId,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VolumeAttachKey(volumeId, requestId), ProviderCommandKinds.VolumeAttach, clusterId,
            new { volumeId, requestId, externalVolumeId, externalVmId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeDetachAsync(Guid volumeId, Guid requestId, string externalVolumeId, string projectName,
        string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VolumeDetachKey(volumeId, requestId), ProviderCommandKinds.VolumeDetach, clusterId,
            new { volumeId, requestId, externalVolumeId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeDeleteAsync(Guid volumeId, Guid requestId, string externalVolumeId, string projectName,
        string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VolumeDeleteKey(volumeId, requestId), ProviderCommandKinds.VolumeDelete, clusterId,
            new { volumeId, requestId, externalVolumeId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeSnapshotCreateAsync(Guid snapshotId, Guid requestId, Guid volumeId, string externalVolumeId,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VolumeSnapshotCreateKey(snapshotId, requestId), ProviderCommandKinds.VolumeSnapshotCreate, clusterId,
            new { snapshotId, requestId, volumeId, externalVolumeId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeSnapshotDeleteAsync(Guid snapshotId, Guid requestId, Guid volumeId, string externalSnapshotId,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VolumeSnapshotDeleteKey(snapshotId, requestId), ProviderCommandKinds.VolumeSnapshotDelete, clusterId,
            new { snapshotId, requestId, volumeId, externalSnapshotId, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeSnapshotRestoreAsync(Guid snapshotId, Guid newVolumeId, Guid requestId,
        string externalSnapshotId, string name, string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(VolumeCreateKey(newVolumeId, requestId), ProviderCommandKinds.VolumeSnapshotRestore, clusterId,
            new { snapshotId, newVolumeId, requestId, externalSnapshotId, name, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // `volume-{kind}-{volumeId}-{requestId}` and `volume-snapshot-{kind}-{snapshotId}-{requestId}`: the stuck-request
    // query rebuilds the key from the waiting request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string VolumeCreateKey(Guid volumeId, Guid requestId) => $"volume-create-{volumeId}-{requestId}";
    internal static string VolumeAttachKey(Guid volumeId, Guid requestId) => $"volume-attach-{volumeId}-{requestId}";
    internal static string VolumeDetachKey(Guid volumeId, Guid requestId) => $"volume-detach-{volumeId}-{requestId}";
    internal static string VolumeDeleteKey(Guid volumeId, Guid requestId) => $"volume-delete-{volumeId}-{requestId}";
    internal static string VolumeSnapshotCreateKey(Guid snapshotId, Guid requestId) => $"volume-snapshot-create-{snapshotId}-{requestId}";
    internal static string VolumeSnapshotDeleteKey(Guid snapshotId, Guid requestId) => $"volume-snapshot-delete-{snapshotId}-{requestId}";

    // AfterFailureOnly: the schedule asks every hour; the day's backup that ran stays done, one that failed runs again.
    public Task EnqueueVmBackupAsync(Guid vmId, string externalVmId, string projectName, Guid tenantId, string clusterId,
        DateOnly day, CancellationToken ct = default) =>
        EnqueueAsync($"vm-backup-{vmId}-{BackupDay(day)}", ProviderCommandKinds.VmBackup, clusterId,
            new { vmId, externalVmId, projectName, tenantId, clusterId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    public Task EnqueueVolumeBackupAsync(Guid volumeId, string externalVolumeId, string projectName, Guid tenantId,
        string clusterId, DateOnly day, CancellationToken ct = default) =>
        EnqueueAsync($"volume-backup-{volumeId}-{BackupDay(day)}", ProviderCommandKinds.VolumeBackup, clusterId,
            new { volumeId, externalVolumeId, projectName, tenantId, clusterId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    // Gregorian digits whatever the host's culture: a Buddhist-calendar culture would write another year into the key.
    private static string BackupDay(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // Load balancer: one row per request, as the VM's (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public Task EnqueueLoadBalancerCreateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(LoadBalancerCreateKey(loadBalancer.Id, requestId), ProviderCommandKinds.LoadBalancerCreate,
            loadBalancer.ClusterId, LoadBalancerPayload(loadBalancer, requestId, intentId: null, projectName),
            ProviderCommandRerun.AfterAnyFinish, ct);

    // intentId carries the request id too, for a worker that still reports the update on the old route with it (removed
    // in rollout phase 6, titan-ops architecture/resource-lifecycle.md#rollout).
    public Task EnqueueLoadBalancerUpdateAsync(LoadBalancer loadBalancer, Guid requestId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync(LoadBalancerUpdateKey(loadBalancer.Id, requestId), ProviderCommandKinds.LoadBalancerUpdate,
            loadBalancer.ClusterId, LoadBalancerPayload(loadBalancer, requestId, intentId: requestId, projectName),
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueLoadBalancerDeleteAsync(Guid loadBalancerId, Guid requestId, string externalNetworkId, string vip,
        string projectName, string clusterId, CancellationToken ct = default) =>
        EnqueueAsync(LoadBalancerDeleteKey(loadBalancerId, requestId), ProviderCommandKinds.LoadBalancerDelete, clusterId,
            new { loadBalancerId, requestId, externalNetworkId, vip, projectName, clusterId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // `load-balancer-{kind}-{loadBalancerId}-{requestId}`: the stuck-request query rebuilds the key from the load
    // balancer's waiting request (titan-ops architecture/resource-lifecycle.md#lost-and-stale).
    internal static string LoadBalancerCreateKey(Guid loadBalancerId, Guid requestId) => $"load-balancer-create-{loadBalancerId}-{requestId}";
    internal static string LoadBalancerUpdateKey(Guid loadBalancerId, Guid requestId) => $"load-balancer-update-{loadBalancerId}-{requestId}";
    internal static string LoadBalancerDeleteKey(Guid loadBalancerId, Guid requestId) => $"load-balancer-delete-{loadBalancerId}-{requestId}";

    // The asked configuration (titan-ops features/load-balancer/design.md#lb-resource-model); protocol in Incus's spelling.
    private static object LoadBalancerPayload(LoadBalancer lb, Guid requestId, Guid? intentId, string projectName) =>
        new
        {
            loadBalancerId = lb.Id,
            requestId,
            intentId,
            projectName,
            clusterId = lb.ClusterId,
            externalNetworkId = lb.ExternalNetworkId,
            publicIpv6Subnet = lb.NetworkSubnet,
            vip = lb.Vip,
            listeners = lb.DesiredListeners
                .OrderBy(l => l.Protocol).ThenBy(l => l.Port)
                .Select(l => new { protocol = l.Protocol.ToString().ToLowerInvariant(), port = l.Port, targetPort = l.TargetPort })
                .ToList(),
            backends = lb.DesiredBackends
                .OrderBy(b => b.ExternalVmId, StringComparer.Ordinal)
                .Select(b => new { vmId = b.VmId, externalVmId = b.ExternalVmId })
                .ToList()
        };

    // One key per template per cluster: keyed on the template alone, the second cluster's sync would be absorbed by the
    // first cluster's open row (titan-ops features/catalog/design.md#template-per-cluster).
    public Task EnqueueTemplateSyncAsync(Guid templateId, string clusterId, string name, string? description,
        string osImageName, string? imageUrl, int? rootDiskSizeGb, CancellationToken ct = default) =>
        EnqueueAsync($"template-sync-{templateId}-{clusterId}", ProviderCommandKinds.TemplateSync, clusterId,
            new { templateId, clusterId, name, description = description ?? "", osImageName, imageUrl = imageUrl ?? "", rootDiskSizeGb },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // A new request on a key of a resource that already exists. The handler read the resource with no such request
    // waiting, so an open row under the key is another request's, committed in between: this one stops with 409
    // before anything of it is saved (titan-ops architecture/provider-and-worker.md#provider-command-key). No key takes
    // this path since every resource's keys carry their request id; it goes with rollout phase 6
    // (titan-ops architecture/resource-lifecycle.md#rollout). Internal so its test runs it on a key of its own.
    internal Task EnqueueRequestAsync(string key, string kind, string clusterId, object payload, ProviderCommandRerun rerun,
        CancellationToken ct) =>
        EnqueueAsync(key, kind, clusterId, payload, rerun, refuseOpenRow: true, ct);

    private Task EnqueueAsync(string key, string kind, string clusterId, object payload, ProviderCommandRerun rerun,
        CancellationToken ct) =>
        EnqueueAsync(key, kind, clusterId, payload, rerun, refuseOpenRow: false, ct);

    private async Task EnqueueAsync(string key, string kind, string clusterId, object payload, ProviderCommandRerun rerun,
        bool refuseOpenRow, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, Json);
        DateTimeOffset now = clock.GetUtcNow();
        ProviderCommand? existing = await repository.FindByKeyAsync(key, ct);
        if (existing == null)
        {
            repository.Add(ProviderCommand.Create(key, kind, clusterId, json, rerun, now));
            return;
        }
        if (refuseOpenRow && existing.IsOpen)
        {
            throw new ConflictException(ErrorStatus.RequestInProgressCode,
                "The same request is already being processed. Read the resource again before retrying.");
        }
        // Absorbed or reopened, per the row's rule; either way the handler's SaveAsync writes the outcome.
        existing.Reopen(json, now);
    }
}
