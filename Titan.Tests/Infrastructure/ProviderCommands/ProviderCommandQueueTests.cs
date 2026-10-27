using System.Text.Json;
using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.ProviderCommands;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure.ProviderCommands;

// https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-key: keys are the former workflow ids, so a repeat keeps
// the shape https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity describes.
public class ProviderCommandQueueTests
{
    private readonly FakeProviderCommandRepository _repo = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));

    private ProviderCommandQueue Queue() => new(_repo, _clock);

    [Fact]
    public async Task VmCreate_StagesOnePendingRow_WithTheProvisionKey_AndTheWorkersPayload()
    {
        Guid vmId = Guid.NewGuid();

        await Queue().EnqueueVmCreateAsync(vmId, "web-1", "m", 2m, 2, 4096, "tpl-a", "mock", "net-1",
            "t-0123", 20, "#cloud-config\n", vendorData: null);

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"vm-provision-{vmId}", row.Key);
        Assert.Equal(ProviderCommandKinds.VmCreate, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(vmId.ToString(), payload.RootElement.GetProperty("vmId").GetString());
        Assert.Equal("t-0123", payload.RootElement.GetProperty("projectName").GetString());
        Assert.Equal("tpl-a", payload.RootElement.GetProperty("templateId").GetString());
        Assert.Equal(4096, payload.RootElement.GetProperty("memoryMb").GetInt32());
    }

    // The worker reads the field as a string, empty meaning "none" (OpenViCloud/titan-incus-worker:resource/vm/create.go).
    [Theory]
    [InlineData("#!/bin/sh\necho hi\n", "#!/bin/sh\necho hi\n")]
    [InlineData(null, "")]
    public async Task VmCreate_CarriesTheVendorData_OrAnEmptyOne(string? vendorData, string expected)
    {
        await Queue().EnqueueVmCreateAsync(Guid.NewGuid(), "web-1", "m", 2m, 2, 4096, "tpl-a", "mock", "net-1",
            "t-0123", 20, null, vendorData);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(expected, payload.RootElement.GetProperty("vendorData").GetString());
        Assert.Equal("", payload.RootElement.GetProperty("userData").GetString());
    }

    // The handler resends a delete that is still waiting: the open row absorbs it.
    [Fact]
    public async Task Resend_OfAnOpenVmDelete_IsAbsorbed_NoSecondRow()
    {
        Guid vmId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmDeleteAsync(vmId, "vm-x", "t-1", "mock");
        await queue.ResendVmDeleteAsync(vmId, "vm-x", "t-1", "mock");

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
    }

    // A request the handler took as new, on a resource key whose row is open: another request committed it first
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-key). Refused before the row is touched, so the
    // handler saves nothing of this request.
    [Theory]
    [InlineData("vm-delete")]
    [InlineData("network-delete")]
    [InlineData("firewall-delete")]
    [InlineData("vm-snapshot-revert")]
    [InlineData("vm-snapshot-delete")]
    [InlineData("volume-attach")]
    [InlineData("volume-detach")]
    [InlineData("volume-delete")]
    [InlineData("volume-snapshot-delete")]
    [InlineData("load-balancer-delete")]
    public async Task NewRequest_OnTheOpenRowOfItsKey_IsRequestInProgress(string key)
    {
        Guid id = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await EnqueueNewAsync(queue, key, id);
        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.StartsWith(key + "-", row.Key);
        string payload = row.Payload;

        ConflictException refused = await Assert.ThrowsAsync<ConflictException>(() => EnqueueNewAsync(queue, key, id));

        Assert.Equal(ErrorStatus.RequestInProgressCode, refused.Code);
        Assert.Same(row, Assert.Single(_repo.Rows));
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        Assert.Equal(payload, row.Payload);
    }

    private static Task EnqueueNewAsync(ProviderCommandQueue queue, string key, Guid id) => key switch
    {
        "vm-delete" => queue.EnqueueVmDeleteAsync(id, "vm-x", "t-1", "mock"),
        "network-delete" => queue.EnqueueNetworkDeleteAsync(id, "net-x", "t-1", "mock"),
        "firewall-delete" => queue.EnqueueFirewallRuleDeleteAsync(id, Guid.Empty, "rule-x", "t-1", "mock"),
        "vm-snapshot-revert" => queue.EnqueueVmSnapshotRevertAsync(id, Guid.Empty, "s-x", "t-1", "mock"),
        "vm-snapshot-delete" => queue.EnqueueVmSnapshotDeleteAsync(id, Guid.Empty, "s-x", "t-1", "mock"),
        "volume-attach" => queue.EnqueueVolumeAttachAsync(id, "vol-x", "vm-x", "t-1", "mock"),
        "volume-detach" => queue.EnqueueVolumeDetachAsync(id, "vol-x", "t-1", "mock"),
        "volume-delete" => queue.EnqueueVolumeDeleteAsync(id, "vol-x", "t-1", "mock"),
        "volume-snapshot-delete" => queue.EnqueueVolumeSnapshotDeleteAsync(id, Guid.Empty, "s-x", "t-1", "mock"),
        "load-balancer-delete" => queue.EnqueueLoadBalancerDeleteAsync(id, "net-x", "2001:db8::1:1", "t-1", "mock"),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "not a resource key"),
    };

    [Fact]
    public async Task VmStop_RepeatAfterItRan_IsAbsorbed()
    {
        Guid vmId = Guid.NewGuid();
        Guid intentId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmStopAsync(vmId, intentId, "vm-x", "t-1", "mock");
        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"vm-stop-{vmId}-{intentId}", row.Key);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Complete(lease, _clock.GetUtcNow());

        await queue.EnqueueVmStopAsync(vmId, intentId, "vm-x", "t-1", "mock");

        Assert.Equal(ProviderCommandStatus.Done, row.Status);
    }

    // A failed run of the same intent is what a repeat may run again, as the failed-only workflow id reuse did.
    [Fact]
    public async Task VmStop_RepeatAfterItFailed_RunsAgain()
    {
        Guid vmId = Guid.NewGuid();
        Guid intentId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmStopAsync(vmId, intentId, "vm-x", "t-1", "mock");
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Fail(lease, "poweroff refused", retryable: false, _clock.GetUtcNow());

        await queue.EnqueueVmStopAsync(vmId, intentId, "vm-x", "t-1", "mock");

        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
    }

    [Fact]
    public async Task VolumeAttach_RepeatAfterItRan_RunsAgain()
    {
        Guid volumeId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVolumeAttachAsync(volumeId, "vol-x", "vm-x", "t-1", "mock");
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Complete(lease, _clock.GetUtcNow());

        await queue.EnqueueVolumeAttachAsync(volumeId, "vol-x", "vm-y", "t-1", "mock");

        Assert.Single(_repo.Rows);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        Assert.Contains("vm-y", row.Payload);
    }

    [Fact]
    public async Task VmDelete_RepeatAfterItRan_IsAbsorbed()
    {
        Guid vmId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmDeleteAsync(vmId, "vm-x", "t-1", "mock");
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Complete(lease, _clock.GetUtcNow());

        await queue.EnqueueVmDeleteAsync(vmId, "vm-x", "t-1", "mock");

        Assert.Equal(ProviderCommandStatus.Done, row.Status);
    }

    // The worker reports a failed stop/start on intents/{intentId}/failed: the id must travel with the command.
    [Fact]
    public async Task StopAndStart_CarryTheIntentIdTheFailureReportNeeds()
    {
        Guid vmId = Guid.NewGuid();
        Guid stopIntent = Guid.NewGuid();
        Guid startIntent = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmStopAsync(vmId, stopIntent, "vm-x", "t-1", "mock");
        await queue.EnqueueVmStartAsync(vmId, startIntent, "vm-x", "t-1", "mock");

        Assert.Contains(stopIntent.ToString(), _repo.Rows[0].Payload);
        Assert.Contains(startIntent.ToString(), _repo.Rows[1].Payload);
        Assert.Equal($"vm-start-{vmId}-{startIntent}", _repo.Rows[1].Key);
    }

    // The delete callback URL is /internal/networks/{networkId}/firewall-rules/{ruleId}.
    [Fact]
    public async Task FirewallRuleDelete_CarriesTheNetworkIdTheCallbackNeeds()
    {
        Guid ruleId = Guid.NewGuid();
        Guid networkId = Guid.NewGuid();

        await Queue().EnqueueFirewallRuleDeleteAsync(ruleId, networkId, "acl-1", "t-1", "mock");

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(networkId.ToString(), payload.RootElement.GetProperty("networkId").GetString());
    }

    // The worker merges the two policies into the network's default action
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/network/design.md#firewall-default-policy): it needs both.
    [Fact]
    public async Task NetworkCreate_CarriesBothPolicies()
    {
        await Queue().EnqueueNetworkCreateAsync(Guid.NewGuid(), "backend", "mock", "t-1", "10.40.0.0/24", "Deny", "Allow", null, null);

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("Deny", payload.RootElement.GetProperty("defaultIngressPolicy").GetString());
        Assert.Equal("Allow", payload.RootElement.GetProperty("defaultEgressPolicy").GetString());
    }

    // The worker reads both as strings, empty meaning "no public IPv6" (OpenViCloud/titan-incus-worker:resource/network/create.go),
    // and builds the network on the uplink with the router at that address (https://github.com/OpenViCloud/titan-ops/blob/main/docs/infra/incus/design.md#ipv6-address-plan).
    [Theory]
    [InlineData("2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170", "2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170")]
    [InlineData(null, null, "", "")]
    public async Task NetworkCreate_CarriesThePublicIpv6_OrEmptyStrings(string? subnet, string? router, string wantSubnet, string wantRouter)
    {
        await Queue().EnqueueNetworkCreateAsync(Guid.NewGuid(), "backend", "singapore01", "t-1", null, "Allow", "Allow", subnet, router);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(wantSubnet, payload.RootElement.GetProperty("publicIpv6Subnet").GetString());
        Assert.Equal(wantRouter, payload.RootElement.GetProperty("publicIpv6RouterAddress").GetString());
    }

    // An old worker would read the removed field; the new payload must not carry it.
    [Fact]
    public async Task VmCreate_NoLongerCarriesEnablePublicIpv6()
    {
        await Queue().EnqueueVmCreateAsync(Guid.NewGuid(), "web-1", "m", 2m, 2, 4096, "tpl-a", "mock", "net-1",
            "t-0123", 20, null, null);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.False(payload.RootElement.TryGetProperty("enablePublicIpv6", out _));
    }

    // A rule lives in its network's ACL, inside the tenant's project (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/network/design.md#firewall-on-incus).
    [Fact]
    public async Task FirewallRuleCreateAndDelete_CarryTheProject()
    {
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueFirewallRuleCreateAsync(Guid.NewGuid(), Guid.NewGuid(), "Ingress", "TCP", 22, 22, "10.0.0.0/8", "net-x", "t-1", "mock");
        await queue.EnqueueFirewallRuleDeleteAsync(Guid.NewGuid(), Guid.NewGuid(), "acl-x/rule", "t-1", "mock");

        foreach (ProviderCommand row in _repo.Rows)
        {
            using JsonDocument payload = JsonDocument.Parse(row.Payload);
            Assert.Equal("t-1", payload.RootElement.GetProperty("projectName").GetString());
            Assert.Equal("mock", payload.RootElement.GetProperty("clusterId").GetString());
        }
        Assert.Equal(2, _repo.Rows.Count);
    }

    // Each restore is its own intent: keyed on the snapshot, a second restore would be absorbed by the first's open row.
    [Fact]
    public async Task VolumeRestore_IsKeyedOnTheNewVolume_NotTheSnapshot()
    {
        Guid snapshotId = Guid.NewGuid();
        Guid firstVolume = Guid.NewGuid();
        Guid secondVolume = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVolumeSnapshotRestoreAsync(snapshotId, firstVolume, "vol-src/snap-1", "a", "t-1", "mock");
        await queue.EnqueueVolumeSnapshotRestoreAsync(snapshotId, secondVolume, "vol-src/snap-1", "b", "t-1", "mock");

        Assert.Equal(2, _repo.Rows.Count);
        Assert.Equal($"volume-snapshot-restore-{secondVolume}", _repo.Rows[1].Key);
    }

    // An Incus image carries no CPU/RAM: sizing travels with the VM (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task TemplateSync_CarriesTheImageAlias_AndNoSizing()
    {
        await Queue().EnqueueTemplateSyncAsync(Guid.NewGuid(), "mock", "Alma-9-DB", null, "almalinux/9", null, 40);

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("almalinux/9", payload.RootElement.GetProperty("osImageName").GetString());
        Assert.False(payload.RootElement.TryGetProperty("cpuUnits", out _));
        Assert.False(payload.RootElement.TryGetProperty("memoryMb", out _));
        Assert.False(payload.RootElement.TryGetProperty("vcpuCount", out _));
    }

    // The worker imports the distro's own image when the template names one; none ⇒ "" and it pulls osImageName
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task TemplateSync_CarriesTheImageUrl_OrAnEmptyOne()
    {
        const string url = "https://cloud-images.ubuntu.com/releases/jammy/release/ubuntu-22.04-server-cloudimg-{arch}.img";
        await Queue().EnqueueTemplateSyncAsync(Guid.NewGuid(), "mock", "Ubuntu-22.04-Standard", null, "ubuntu/22.04", url, 25);
        await Queue().EnqueueTemplateSyncAsync(Guid.NewGuid(), "mock", "Alma-9-DB", null, "almalinux/9", null, 40);

        Assert.Equal(2, _repo.Rows.Count);
        using JsonDocument withUrl = JsonDocument.Parse(_repo.Rows[0].Payload);
        using JsonDocument withoutUrl = JsonDocument.Parse(_repo.Rows[1].Payload);
        Assert.Equal(url, withUrl.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("", withoutUrl.RootElement.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task TemplateSync_IsKeyedPerTemplatePerCluster()
    {
        Guid templateId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueTemplateSyncAsync(templateId, "singapore01", "Alma-9-DB", null, "almalinux/9", null, 40);
        await queue.EnqueueTemplateSyncAsync(templateId, "mock", "Alma-9-DB", null, "almalinux/9", null, 40);

        Assert.Equal(
            new[] { $"template-sync-{templateId}-singapore01", $"template-sync-{templateId}-mock" },
            _repo.Rows.Select(r => r.Key).ToArray());
    }

    // One backup a day per resource (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#incus-backup):
    // the UTC day is in the key, and the payload carries what the worker needs to name the object in the bucket.
    [Fact]
    public async Task VmBackup_KeyIsTheVmAndTheDay_PayloadCarriesTenantAndProject()
    {
        Guid vmId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();

        await Queue().EnqueueVmBackupAsync(vmId, "vm-x", "t-0123", tenantId, "mock", new DateOnly(2026, 10, 26));

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"vm-backup-{vmId}-20261026", row.Key);
        Assert.Equal(ProviderCommandKinds.VmBackup, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(vmId.ToString(), payload.RootElement.GetProperty("vmId").GetString());
        Assert.Equal("vm-x", payload.RootElement.GetProperty("externalVmId").GetString());
        Assert.Equal("t-0123", payload.RootElement.GetProperty("projectName").GetString());
        Assert.Equal(tenantId.ToString(), payload.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task VolumeBackup_KeyIsTheVolumeAndTheDay_PayloadCarriesTenantAndProject()
    {
        Guid volumeId = Guid.NewGuid();
        Guid tenantId = Guid.NewGuid();

        await Queue().EnqueueVolumeBackupAsync(volumeId, "vol-x", "t-0123", tenantId, "mock", new DateOnly(2026, 10, 26));

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"volume-backup-{volumeId}-20261026", row.Key);
        Assert.Equal(ProviderCommandKinds.VolumeBackup, row.Kind);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(volumeId.ToString(), payload.RootElement.GetProperty("volumeId").GetString());
        Assert.Equal("vol-x", payload.RootElement.GetProperty("externalVolumeId").GetString());
        Assert.Equal(tenantId.ToString(), payload.RootElement.GetProperty("tenantId").GetString());
    }

    // The schedule enqueues again every hour: a backup of the day that ran is not run again, one that failed is.
    [Fact]
    public async Task VmBackup_SameDayAfterItRan_IsNotRunAgain()
    {
        Guid vmId = Guid.NewGuid();
        DateOnly day = new(2026, 10, 26);
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", day);
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Complete(lease, _clock.GetUtcNow());

        await queue.EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", day);

        Assert.Equal(ProviderCommandStatus.Done, Assert.Single(_repo.Rows).Status);
    }

    [Fact]
    public async Task VmBackup_SameDayAfterItFailed_RunsAgain()
    {
        Guid vmId = Guid.NewGuid();
        DateOnly day = new(2026, 10, 26);
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", day);
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Fail(lease, "upload refused", retryable: false, _clock.GetUtcNow());

        await queue.EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", day);

        Assert.Equal(ProviderCommandStatus.Pending, Assert.Single(_repo.Rows).Status);
    }

    [Fact]
    public async Task VmBackup_KeyHasTheGregorianDay_UnderABuddhistCalendarCulture()
    {
        Guid vmId = Guid.NewGuid();
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
        try
        {
            await Queue().EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", new DateOnly(2026, 10, 26));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }

        Assert.Equal($"vm-backup-{vmId}-20261026", Assert.Single(_repo.Rows).Key);
    }

    [Fact]
    public async Task VmBackup_NextDay_IsANewRow()
    {
        Guid vmId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", new DateOnly(2026, 10, 26));
        await queue.EnqueueVmBackupAsync(vmId, "vm-x", "t-1", Guid.NewGuid(), "mock", new DateOnly(2026, 10, 27));

        Assert.Equal(2, _repo.Rows.Count);
    }

    // Incus keeps volumes and snapshots inside the tenant's project: a storage or snapshot command that does not say
    // which project cannot be carried out (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#translate-titan-ids).
    public static TheoryData<string> StorageAndSnapshotKinds() => new()
    {
        ProviderCommandKinds.VolumeCreate, ProviderCommandKinds.VolumeAttach, ProviderCommandKinds.VolumeDetach,
        ProviderCommandKinds.VolumeDelete, ProviderCommandKinds.VolumeSnapshotCreate,
        ProviderCommandKinds.VolumeSnapshotDelete, ProviderCommandKinds.VolumeSnapshotRestore,
        ProviderCommandKinds.VmSnapshotCreate, ProviderCommandKinds.VmSnapshotRevert, ProviderCommandKinds.VmSnapshotDelete,
        ProviderCommandKinds.VmBackup, ProviderCommandKinds.VolumeBackup,
    };

    [Theory]
    [MemberData(nameof(StorageAndSnapshotKinds))]
    public async Task StorageAndSnapshotCommands_CarryTheTenantsProject_AndTheCluster(string kind)
    {
        await EnqueueOne(Queue(), kind, project: "t-0123", cluster: "mock");

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal(kind, row.Kind);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("t-0123", payload.RootElement.GetProperty("projectName").GetString());
        Assert.Equal("mock", payload.RootElement.GetProperty("clusterId").GetString());
    }

    // The worker has no catalog: an offering id is a Titan id the provider cannot use; the pool comes from the cluster.
    [Fact]
    public async Task VolumeCreate_CarriesTheSize_NotTheOfferingId()
    {
        await Queue().EnqueueVolumeCreateAsync(Guid.NewGuid(), "data-1", "mock", 50, "t-0123");

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(50, payload.RootElement.GetProperty("sizeGb").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("volumeOfferingId", out _));
    }

    private static Task EnqueueOne(ProviderCommandQueue queue, string kind, string project, string cluster)
    {
        Guid id = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        switch (kind)
        {
            case ProviderCommandKinds.VolumeCreate:
                return queue.EnqueueVolumeCreateAsync(id, "data-1", cluster, 10, project);
            case ProviderCommandKinds.VolumeAttach:
                return queue.EnqueueVolumeAttachAsync(id, "vol-x", "vm-x", project, cluster);
            case ProviderCommandKinds.VolumeDetach:
                return queue.EnqueueVolumeDetachAsync(id, "vol-x", project, cluster);
            case ProviderCommandKinds.VolumeDelete:
                return queue.EnqueueVolumeDeleteAsync(id, "vol-x", project, cluster);
            case ProviderCommandKinds.VolumeSnapshotCreate:
                return queue.EnqueueVolumeSnapshotCreateAsync(id, other, "vol-x", project, cluster);
            case ProviderCommandKinds.VolumeSnapshotDelete:
                return queue.EnqueueVolumeSnapshotDeleteAsync(id, other, "vol-x/snap-x", project, cluster);
            case ProviderCommandKinds.VolumeSnapshotRestore:
                return queue.EnqueueVolumeSnapshotRestoreAsync(id, other, "vol-x/snap-x", "restored", project, cluster);
            case ProviderCommandKinds.VmSnapshotCreate:
                return queue.EnqueueVmSnapshotCreateAsync(id, other, "vm-x", "before-upgrade", project, cluster);
            case ProviderCommandKinds.VmSnapshotRevert:
                return queue.EnqueueVmSnapshotRevertAsync(id, other, "vm-x/snap-x", project, cluster);
            case ProviderCommandKinds.VmSnapshotDelete:
                return queue.EnqueueVmSnapshotDeleteAsync(id, other, "vm-x/snap-x", project, cluster);
            case ProviderCommandKinds.VmBackup:
                return queue.EnqueueVmBackupAsync(id, "vm-x", project, other, cluster, new DateOnly(2026, 10, 26));
            case ProviderCommandKinds.VolumeBackup:
                return queue.EnqueueVolumeBackupAsync(id, "vol-x", project, other, cluster, new DateOnly(2026, 10, 26));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a storage or snapshot kind");
        }
    }
}
