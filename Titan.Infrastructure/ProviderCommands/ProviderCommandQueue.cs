using System.Globalization;
using System.Text.Json;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Endpoints;

namespace Titan.Infrastructure.ProviderCommands;

/// Stages provider commands in the scope's DbContext (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table).
/// Keys are the former workflow ids, so the repeat rules of https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity
/// keep their shape; payload property names are what the worker reads.
public class ProviderCommandQueue(IProviderCommandRepository repository, TimeProvider clock) : IProviderCommandQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task EnqueueNetworkCreateAsync(Guid networkId, string name, string zoneId, string projectName,
        string? cidr, string defaultIngressPolicy, string defaultEgressPolicy, string? publicIpv6Subnet,
        string? publicIpv6RouterAddress, CancellationToken ct = default) =>
        EnqueueAsync($"network-create-{networkId}", ProviderCommandKinds.NetworkCreate, zoneId,
            new
            {
                networkId, name, projectName, zoneId, cidr = cidr ?? "", defaultIngressPolicy, defaultEgressPolicy,
                publicIpv6Subnet = publicIpv6Subnet ?? "", publicIpv6RouterAddress = publicIpv6RouterAddress ?? ""
            },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueNetworkDeleteAsync(Guid networkId, string externalNetworkId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueRequestAsync($"network-delete-{networkId}", ProviderCommandKinds.NetworkDelete, zoneId,
            new { networkId, externalNetworkId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueFirewallRuleCreateAsync(Guid ruleId, Guid networkId, string direction, string protocol,
        int? startPort, int? endPort, string cidr, string externalNetworkId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueAsync($"firewall-create-{ruleId}", ProviderCommandKinds.FirewallRuleCreate, zoneId,
            new { ruleId, networkId, direction, protocol, startPort, endPort, cidr, externalNetworkId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // networkId: the worker needs it for the callback URL that reports the delete (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#forget-on-provider-confirm).
    public Task EnqueueFirewallRuleDeleteAsync(Guid ruleId, Guid networkId, string externalRuleId, string projectName,
        string zoneId, CancellationToken ct = default) =>
        EnqueueRequestAsync($"firewall-delete-{ruleId}", ProviderCommandKinds.FirewallRuleDelete, zoneId,
            new { ruleId, networkId, externalRuleId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmCreateAsync(Guid vmId, string name, string vmOfferingId, decimal cpuUnits, int vcpuCount,
        int memoryMb, string templateId, string zoneId, string externalNetworkId, string projectName, int diskSizeGb,
        string? userData, string? vendorData, CancellationToken ct = default) =>
        EnqueueAsync($"vm-provision-{vmId}", ProviderCommandKinds.VmCreate, zoneId,
            new
            {
                vmId, name, vmOfferingId, cpuUnits, vcpuCount, memoryMb, templateId, zoneId, externalNetworkId,
                projectName, diskSizeGb, userData = userData ?? "", vendorData = vendorData ?? ""
            },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // AfterFailureOnly: a delete that already happened must not run again; one that gave up may.
    public Task EnqueueVmDeleteAsync(Guid vmId, string externalVmId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueRequestAsync($"vm-delete-{vmId}", ProviderCommandKinds.VmDelete, zoneId,
            new { vmId, externalVmId, projectName, zoneId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    // The handler's own repeat of a delete still waiting: the open row is that delete, and absorbs it.
    public Task ResendVmDeleteAsync(Guid vmId, string externalVmId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueAsync($"vm-delete-{vmId}", ProviderCommandKinds.VmDelete, zoneId,
            new { vmId, externalVmId, projectName, zoneId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    // The key carries the intent (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity): a repeat of an open
    // or finished intent is absorbed, a failed one runs again, and stop→start→stop gets a new key.
    public Task EnqueueVmStopAsync(Guid vmId, Guid intentId, string externalVmId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueAsync(VmStopKey(vmId, intentId), ProviderCommandKinds.VmStop, zoneId,
            new { vmId, intentId, externalVmId, projectName, zoneId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    public Task EnqueueVmStartAsync(Guid vmId, Guid intentId, string externalVmId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueAsync(VmStartKey(vmId, intentId), ProviderCommandKinds.VmStart, zoneId,
            new { vmId, intentId, externalVmId, projectName, zoneId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    internal static string VmStopKey(Guid vmId, Guid intentId) => $"vm-stop-{vmId}-{intentId}";
    internal static string VmStartKey(Guid vmId, Guid intentId) => $"vm-start-{vmId}-{intentId}";

    public Task EnqueueVmSnapshotCreateAsync(Guid snapshotId, Guid vmId, string externalVmId, string name,
        string projectName, string zoneId, CancellationToken ct = default) =>
        EnqueueAsync($"vm-snapshot-create-{snapshotId}", ProviderCommandKinds.VmSnapshotCreate, zoneId,
            new { snapshotId, vmId, externalVmId, name, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmSnapshotRevertAsync(Guid snapshotId, Guid vmId, string externalSnapshotId, string projectName,
        string zoneId, CancellationToken ct = default) =>
        EnqueueRequestAsync($"vm-snapshot-revert-{snapshotId}", ProviderCommandKinds.VmSnapshotRevert, zoneId,
            new { snapshotId, vmId, externalSnapshotId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVmSnapshotDeleteAsync(Guid snapshotId, Guid vmId, string externalSnapshotId, string projectName,
        string zoneId, CancellationToken ct = default) =>
        EnqueueRequestAsync($"vm-snapshot-delete-{snapshotId}", ProviderCommandKinds.VmSnapshotDelete, zoneId,
            new { snapshotId, vmId, externalSnapshotId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeCreateAsync(Guid volumeId, string name, string zoneId, int sizeGb,
        string projectName, CancellationToken ct = default) =>
        EnqueueAsync($"volume-create-{volumeId}", ProviderCommandKinds.VolumeCreate, zoneId,
            new { volumeId, name, zoneId, sizeGb, projectName },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeAttachAsync(Guid volumeId, string externalVolumeId, string externalVmId, string projectName,
        string zoneId, CancellationToken ct = default) =>
        EnqueueRequestAsync($"volume-attach-{volumeId}", ProviderCommandKinds.VolumeAttach, zoneId,
            new { volumeId, externalVolumeId, externalVmId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeDetachAsync(Guid volumeId, string externalVolumeId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueRequestAsync($"volume-detach-{volumeId}", ProviderCommandKinds.VolumeDetach, zoneId,
            new { volumeId, externalVolumeId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeDeleteAsync(Guid volumeId, string externalVolumeId, string projectName, string zoneId,
        CancellationToken ct = default) =>
        EnqueueRequestAsync($"volume-delete-{volumeId}", ProviderCommandKinds.VolumeDelete, zoneId,
            new { volumeId, externalVolumeId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeSnapshotCreateAsync(Guid snapshotId, Guid volumeId, string externalVolumeId,
        string projectName, string zoneId, CancellationToken ct = default) =>
        EnqueueAsync($"volume-snapshot-create-{snapshotId}", ProviderCommandKinds.VolumeSnapshotCreate, zoneId,
            new { snapshotId, volumeId, externalVolumeId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueVolumeSnapshotDeleteAsync(Guid snapshotId, Guid volumeId, string externalSnapshotId,
        string projectName, string zoneId, CancellationToken ct = default) =>
        EnqueueRequestAsync($"volume-snapshot-delete-{snapshotId}", ProviderCommandKinds.VolumeSnapshotDelete, zoneId,
            new { snapshotId, volumeId, externalSnapshotId, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // Keyed on the new volume, not the snapshot: each restore is its own intent, and a second restore keyed on the
    // snapshot would be absorbed while the first is open, leaving its volume Allocating for ever.
    public Task EnqueueVolumeSnapshotRestoreAsync(Guid snapshotId, Guid newVolumeId, string externalSnapshotId,
        string name, string projectName, string zoneId, CancellationToken ct = default) =>
        EnqueueAsync($"volume-snapshot-restore-{newVolumeId}", ProviderCommandKinds.VolumeSnapshotRestore, zoneId,
            new { snapshotId, newVolumeId, externalSnapshotId, name, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // AfterFailureOnly: the schedule asks every hour; the day's backup that ran stays done, one that failed runs again.
    public Task EnqueueVmBackupAsync(Guid vmId, string externalVmId, string projectName, Guid tenantId, string zoneId,
        DateOnly day, CancellationToken ct = default) =>
        EnqueueAsync($"vm-backup-{vmId}-{BackupDay(day)}", ProviderCommandKinds.VmBackup, zoneId,
            new { vmId, externalVmId, projectName, tenantId, zoneId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    public Task EnqueueVolumeBackupAsync(Guid volumeId, string externalVolumeId, string projectName, Guid tenantId,
        string zoneId, DateOnly day, CancellationToken ct = default) =>
        EnqueueAsync($"volume-backup-{volumeId}-{BackupDay(day)}", ProviderCommandKinds.VolumeBackup, zoneId,
            new { volumeId, externalVolumeId, projectName, tenantId, zoneId },
            ProviderCommandRerun.AfterFailureOnly, ct);

    // Gregorian digits whatever the host's culture: a Buddhist-calendar culture would write another year into the key.
    private static string BackupDay(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // The update is keyed on its intent and runs again only after a failure, like stop and start: an update that
    // already happened must not run twice, and a second update gets its own key.
    public Task EnqueueLoadBalancerCreateAsync(LoadBalancer loadBalancer, string projectName, CancellationToken ct = default) =>
        EnqueueAsync($"load-balancer-create-{loadBalancer.Id}", ProviderCommandKinds.LoadBalancerCreate,
            loadBalancer.ZoneId, LoadBalancerPayload(loadBalancer, null, projectName), ProviderCommandRerun.AfterAnyFinish, ct);

    public Task EnqueueLoadBalancerUpdateAsync(LoadBalancer loadBalancer, Guid intentId, string projectName,
        CancellationToken ct = default) =>
        EnqueueAsync($"load-balancer-update-{loadBalancer.Id}-{intentId}", ProviderCommandKinds.LoadBalancerUpdate,
            loadBalancer.ZoneId, LoadBalancerPayload(loadBalancer, intentId, projectName), ProviderCommandRerun.AfterFailureOnly, ct);

    public Task EnqueueLoadBalancerDeleteAsync(Guid loadBalancerId, string externalNetworkId, string vip,
        string projectName, string zoneId, CancellationToken ct = default) =>
        EnqueueRequestAsync($"load-balancer-delete-{loadBalancerId}", ProviderCommandKinds.LoadBalancerDelete, zoneId,
            new { loadBalancerId, externalNetworkId, vip, projectName, zoneId },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // The asked configuration (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-resource-model); protocol in Incus's spelling.
    private static object LoadBalancerPayload(LoadBalancer lb, Guid? intentId, string projectName) =>
        new
        {
            loadBalancerId = lb.Id,
            intentId,
            projectName,
            zoneId = lb.ZoneId,
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

    // One key per template per zone: keyed on the template alone, the second zone's sync would be absorbed by the
    // first zone's open row (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-zone).
    public Task EnqueueTemplateSyncAsync(Guid templateId, string zoneId, string name, string? description,
        string osImageName, string? imageUrl, int? rootDiskSizeGb, CancellationToken ct = default) =>
        EnqueueAsync($"template-sync-{templateId}-{zoneId}", ProviderCommandKinds.TemplateSync, zoneId,
            new { templateId, zoneId, name, description = description ?? "", osImageName, imageUrl = imageUrl ?? "", rootDiskSizeGb },
            ProviderCommandRerun.AfterAnyFinish, ct);

    // A new request on a key of a resource that already exists. The handler read the resource with no such request
    // waiting, so an open row under the key is another request's, committed in between: this one stops with 409
    // before anything of it is saved (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-key).
    private Task EnqueueRequestAsync(string key, string kind, string zoneId, object payload, ProviderCommandRerun rerun,
        CancellationToken ct) =>
        EnqueueAsync(key, kind, zoneId, payload, rerun, refuseOpenRow: true, ct);

    private Task EnqueueAsync(string key, string kind, string zoneId, object payload, ProviderCommandRerun rerun,
        CancellationToken ct) =>
        EnqueueAsync(key, kind, zoneId, payload, rerun, refuseOpenRow: false, ct);

    private async Task EnqueueAsync(string key, string kind, string zoneId, object payload, ProviderCommandRerun rerun,
        bool refuseOpenRow, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, Json);
        DateTimeOffset now = clock.GetUtcNow();
        ProviderCommand? existing = await repository.FindByKeyAsync(key, ct);
        if (existing == null)
        {
            repository.Add(ProviderCommand.Create(key, kind, zoneId, json, rerun, now));
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
