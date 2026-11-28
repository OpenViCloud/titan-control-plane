using System.Text.Json;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.ProviderCommands;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure.ProviderCommands;

// The keys of a resource carry the request id, a backup's the day, and a repeat of a key follows one rule
// (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
public class ProviderCommandQueueTests
{
    private readonly FakeProviderCommandRepository _repo = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));

    private ProviderCommandQueue Queue() => new(_repo, _clock);

    // The VM and the tier the queue reads a VM payload from (titan-ops features/catalog/design.md#catalog-resolution-at-create).
    private static Vm AVm(string clusterId = "mock", string? userData = "#cloud-config\n") =>
        Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", "tpl-guid", clusterId, 20, userData);

    private static readonly VmOffering Medium = VmOffering.Create("m", "m", 2, 4096, 1m);

    // A restore copies a VM or volume Incus holds (titan-ops features/backup/design.md#restore-as-new-resource): the
    // source, and the new row the admin's handler makes of it.
    private static (Vm Source, Vm Copy) ARestoredVm(string clusterId = "mock")
    {
        Vm source = AVm(clusterId);
        source.MarkRunning("vm-x", "10.0.0.5");
        return (source, Vm.RestoreFromBackup(source, "web-1-restored"));
    }

    private static (Volume Source, Volume Copy) ARestoredVolume(string clusterId = "mock")
    {
        Volume source = AVolume(clusterId);
        source.MarkCreated("vol-x");
        return (source, Volume.RestoreFromBackup(source, "data-1-restored"));
    }

    private const string ArchiveKey = "20261026T020709Z";

    // The resource of the "resource" key shape: the same VM each call, so the same key.
    private readonly Vm _vmOfTheSameKey = AVm();

    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    private static Network ANetwork(string clusterId = "mock", FirewallPolicy ingress = FirewallPolicy.Allow,
        FirewallPolicy egress = FirewallPolicy.Allow, string? cidr = null) =>
        TestAggregates.Network(Owner, "backend", clusterId, ingress, egress, cidr);

    // A rule goes only on a network Incus has created (Network.AddFirewallRule).
    private static (Network Network, FirewallRule Rule) ARule()
    {
        Network network = ANetwork();
        network.MarkActive("net-x", "10.40.0.0/24", "10.40.0.1");
        FirewallRule rule = network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "10.0.0.0/8");
        return (network, rule);
    }

    private static Volume AVolume(string clusterId = "mock") => Volume.Create(Owner.Id, "data-1", clusterId, "std", 50);

    private static (Volume Volume, VolumeSnapshot Snapshot) AVolumeSnapshot()
    {
        Volume volume = AVolume();
        volume.MarkCreated("vol-x");
        return (volume, volume.WithSnapshot("nightly"));
    }

    private static (Vm Vm, VmSnapshot Snapshot) AVmSnapshot()
    {
        Vm vm = AVm();
        vm.MarkRunning("vm-x", "10.0.0.5");
        return (vm, vm.WithSnapshot("nightly"));
    }

    private static (Vm Vm, VmKeyAddition Addition) AVmKeyAddition()
    {
        Vm vm = AVm();
        vm.MarkRunning("vm-x", "10.0.0.5");
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        return (vm, vm.KeyAdditions.Single());
    }

    private static VmTemplate ATemplate(string? imageUrl = null)
    {
        VmTemplate template = VmTemplate.Create("Alma-9-DB", "a description", "almalinux/9", 40, "m");
        if (imageUrl != null)
        {
            TemplateSeedFields.WithImageUrl(template, imageUrl);
        }
        return template;
    }

    private const string RuntimeImage =
        "ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:3f12d5472d95dfb85ad20ba7d970eccfdbf25d4073a9ee46b71e7434d8eb1fcd";

    private static FunctionRuntime ARuntime() => FunctionRuntime.Create("nodejs26", RuntimeImage, "amd64", new DateOnly(2029, 4, 30));

    private static (Function Function, FunctionVersion Version) AFunctionVersion(string clusterId = "fn-dev", string? handler = "main.handler")
    {
        Function function = TestFunctions.AFunction(clusterId: clusterId);
        return (function, function.WithVersion(handler));
    }

    // Every kind stages the tenant whose resource it acts on, and template.sync none: a tenant's commands run one at a
    // time in each lane (titan-ops features/provider/design.md#tenant-lane). A new kind fails the first assertion until
    // it is here.
    [Fact]
    public async Task EveryKind_StagesTheTenantOfItsResource_AndTemplateSyncNoTenant()
    {
        ProviderCommandQueue queue = Queue();
        (Vm vm, VmSnapshot vmSnapshot) = AVmSnapshot();
        (Vm keyAdditionVm, VmKeyAddition keyAddition) = AVmKeyAddition();
        (Volume volume, VolumeSnapshot volumeSnapshot) = AVolumeSnapshot();
        (Network network, FirewallRule rule) = ARule();
        LoadBalancer lb = ALoadBalancer();
        (Function function, FunctionVersion version) = AFunctionVersion();
        Vm restoredVm = Vm.RestoreFromBackup(vm, "web-1-restored");
        Volume restoredVolume = Volume.RestoreFromBackup(volume, "data-1-restored");
        var expected = new Dictionary<string, Guid?>
        {
            [ProviderCommandKinds.VmCreate] = vm.TenantId, [ProviderCommandKinds.VmDelete] = vm.TenantId,
            [ProviderCommandKinds.VmStop] = vm.TenantId, [ProviderCommandKinds.VmStart] = vm.TenantId,
            [ProviderCommandKinds.VmSnapshotCreate] = vm.TenantId, [ProviderCommandKinds.VmSnapshotRevert] = vm.TenantId,
            [ProviderCommandKinds.VmSnapshotDelete] = vm.TenantId,
            [ProviderCommandKinds.VmKeyAdditionCreate] = keyAdditionVm.TenantId,
            [ProviderCommandKinds.VmBackup] = vm.TenantId,
            [ProviderCommandKinds.NetworkCreate] = network.TenantId, [ProviderCommandKinds.NetworkDelete] = network.TenantId,
            [ProviderCommandKinds.FirewallRuleCreate] = network.TenantId,
            [ProviderCommandKinds.FirewallRuleDelete] = network.TenantId,
            [ProviderCommandKinds.VolumeCreate] = volume.TenantId, [ProviderCommandKinds.VolumeAttach] = volume.TenantId,
            [ProviderCommandKinds.VolumeDetach] = volume.TenantId, [ProviderCommandKinds.VolumeDelete] = volume.TenantId,
            [ProviderCommandKinds.VolumeSnapshotCreate] = volume.TenantId,
            [ProviderCommandKinds.VolumeSnapshotDelete] = volume.TenantId,
            [ProviderCommandKinds.VolumeSnapshotRestore] = volume.TenantId,
            [ProviderCommandKinds.VolumeBackup] = volume.TenantId,
            [ProviderCommandKinds.LoadBalancerCreate] = lb.TenantId, [ProviderCommandKinds.LoadBalancerUpdate] = lb.TenantId,
            [ProviderCommandKinds.LoadBalancerDelete] = lb.TenantId,
            [ProviderCommandKinds.TemplateSync] = null,
            [ProviderCommandKinds.FunctionRuntimeSync] = null,
            // In the tenant's function lane (ProviderCommandKinds.Function).
            [ProviderCommandKinds.FunctionDeploy] = function.TenantId, [ProviderCommandKinds.FunctionDelete] = function.TenantId,
            [ProviderCommandKinds.ClusterBackup] = null,
            [ProviderCommandKinds.RestoreDrill] = null,
            // Leased on the drill's loop: a row with the tenant would sit in that tenant's command lane.
            [ProviderCommandKinds.VmRestore] = null,
            [ProviderCommandKinds.VolumeRestore] = null,
        };

        await queue.EnqueueVmCreateAsync(vm, Guid.NewGuid(), Medium, "tpl-a", "net-1", "t-1", vendorData: null);
        await queue.EnqueueVmDeleteAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmStopAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmStartAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmSnapshotCreateAsync(vm, vmSnapshot, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmSnapshotRevertAsync(vm, vmSnapshot, Guid.NewGuid(), "vm-x/s-x", "t-1");
        await queue.EnqueueVmSnapshotDeleteAsync(vm, vmSnapshot, Guid.NewGuid(), "vm-x/s-x", "t-1");
        await queue.EnqueueVmKeyAdditionCreateAsync(keyAdditionVm, keyAddition, Guid.NewGuid(), "vm-x", "t-1", ["ssh-ed25519 AAAA"]);
        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", new DateOnly(2026, 11, 11));
        await queue.EnqueueNetworkCreateAsync(network, Guid.NewGuid(), "t-1", null);
        await queue.EnqueueNetworkDeleteAsync(network, Guid.NewGuid(), "net-x", "t-1");
        await queue.EnqueueFirewallRuleCreateAsync(network, rule, Guid.NewGuid(), "t-1");
        await queue.EnqueueFirewallRuleDeleteAsync(network, rule, Guid.NewGuid(), "acl-x/rule", "t-1");
        await queue.EnqueueVolumeCreateAsync(volume, Guid.NewGuid(), "t-1");
        await queue.EnqueueVolumeAttachAsync(volume, Guid.NewGuid(), "vol-x", "vm-x", "t-1");
        await queue.EnqueueVolumeDetachAsync(volume, Guid.NewGuid(), "vol-x", "t-1");
        await queue.EnqueueVolumeDeleteAsync(volume, Guid.NewGuid(), "vol-x", "t-1");
        await queue.EnqueueVolumeSnapshotCreateAsync(volume, volumeSnapshot, Guid.NewGuid(), "vol-x", "t-1");
        await queue.EnqueueVolumeSnapshotDeleteAsync(volume, volumeSnapshot, Guid.NewGuid(), "vol-x/s-x", "t-1");
        await queue.EnqueueVolumeSnapshotRestoreAsync(volume, AVolumeSnapshot().Item1, Guid.NewGuid(), "vol-x/s-x", "t-1");
        await queue.EnqueueVolumeBackupAsync(volume, "vol-x", "t-1", new DateOnly(2026, 11, 11));
        await queue.EnqueueLoadBalancerCreateAsync(lb, Guid.NewGuid(), "t-1");
        await queue.EnqueueLoadBalancerUpdateAsync(lb, Guid.NewGuid(), "t-1");
        await queue.EnqueueLoadBalancerDeleteAsync(lb, Guid.NewGuid(), lb.Vip, "t-1");
        await queue.EnqueueTemplateSyncAsync(ATemplate(), "mock", Guid.NewGuid());
        await queue.EnqueueFunctionRuntimeSyncAsync(ARuntime(), "fn-dev", Guid.NewGuid());
        await queue.EnqueueFunctionDeployAsync(function, version, Guid.NewGuid());
        await queue.EnqueueFunctionDeleteAsync(function, Guid.NewGuid());
        await queue.EnqueueClusterBackupAsync("mock", new DateOnly(2026, 11, 11));
        await queue.EnqueueRestoreDrillAsync("mock", new DateOnly(2026, 11, 11), "vm", Guid.NewGuid(), "vm-x", "web-1", "t-1", Guid.NewGuid());
        await queue.EnqueueVmRestoreAsync(vm, restoredVm, Guid.NewGuid(), ArchiveKey, "t-1", []);
        await queue.EnqueueVolumeRestoreAsync(volume, restoredVolume, Guid.NewGuid(), ArchiveKey, "t-1");

        string[] everyKind = typeof(ProviderCommandKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.Equal(everyKind.Order(StringComparer.Ordinal), _repo.Rows.Select(r => r.Kind).Order(StringComparer.Ordinal));
        Assert.All(_repo.Rows, row => Assert.Equal(expected[row.Kind], row.TenantId));
    }

    // One leased command per VM or volume across both lanes (titan-ops features/provider/design.md#tenant-lane): every
    // kind of a VM or volume names it, a restore names the volume it copies from, the rest name none. A new kind fails
    // the lookup until it is here.
    [Fact]
    public async Task EveryKind_StagesTheVmOrVolumeItActsOn_AndTheOthersNone()
    {
        ProviderCommandQueue queue = Queue();
        (Vm vm, VmSnapshot vmSnapshot) = AVmSnapshot();
        (Vm keyAdditionVm, VmKeyAddition keyAddition) = AVmKeyAddition();
        (Volume volume, VolumeSnapshot volumeSnapshot) = AVolumeSnapshot();
        Volume restored = AVolumeSnapshot().Item1;
        (Network network, FirewallRule rule) = ARule();
        LoadBalancer lb = ALoadBalancer();
        (Function function, FunctionVersion version) = AFunctionVersion();
        Vm restoredVm = Vm.RestoreFromBackup(vm, "web-1-restored");
        Volume restoredVolume = Volume.RestoreFromBackup(volume, "data-1-restored");
        var expected = new Dictionary<string, Guid?>
        {
            [ProviderCommandKinds.VmCreate] = vm.Id, [ProviderCommandKinds.VmDelete] = vm.Id,
            [ProviderCommandKinds.VmStop] = vm.Id, [ProviderCommandKinds.VmStart] = vm.Id,
            [ProviderCommandKinds.VmSnapshotCreate] = vm.Id, [ProviderCommandKinds.VmSnapshotRevert] = vm.Id,
            [ProviderCommandKinds.VmSnapshotDelete] = vm.Id,
            [ProviderCommandKinds.VmKeyAdditionCreate] = keyAdditionVm.Id,
            [ProviderCommandKinds.VmBackup] = vm.Id,
            [ProviderCommandKinds.NetworkCreate] = null, [ProviderCommandKinds.NetworkDelete] = null,
            [ProviderCommandKinds.FirewallRuleCreate] = null, [ProviderCommandKinds.FirewallRuleDelete] = null,
            [ProviderCommandKinds.VolumeCreate] = volume.Id, [ProviderCommandKinds.VolumeAttach] = volume.Id,
            [ProviderCommandKinds.VolumeDetach] = volume.Id, [ProviderCommandKinds.VolumeDelete] = volume.Id,
            [ProviderCommandKinds.VolumeSnapshotCreate] = volume.Id,
            [ProviderCommandKinds.VolumeSnapshotDelete] = volume.Id,
            [ProviderCommandKinds.VolumeSnapshotRestore] = volume.Id,
            [ProviderCommandKinds.VolumeBackup] = volume.Id,
            [ProviderCommandKinds.LoadBalancerCreate] = null, [ProviderCommandKinds.LoadBalancerUpdate] = null,
            [ProviderCommandKinds.LoadBalancerDelete] = null,
            [ProviderCommandKinds.TemplateSync] = null,
            [ProviderCommandKinds.FunctionRuntimeSync] = null,
            // A deploy and a delete of one function never run at once.
            [ProviderCommandKinds.FunctionDeploy] = function.Id, [ProviderCommandKinds.FunctionDelete] = function.Id,
            [ProviderCommandKinds.ClusterBackup] = null,
            [ProviderCommandKinds.RestoreDrill] = null,
            // The row holds the new VM or volume, whose create it is; the source keeps its own commands and backups.
            [ProviderCommandKinds.VmRestore] = restoredVm.Id,
            [ProviderCommandKinds.VolumeRestore] = restoredVolume.Id,
        };

        await queue.EnqueueVmCreateAsync(vm, Guid.NewGuid(), Medium, "tpl-a", "net-1", "t-1", vendorData: null);
        await queue.EnqueueVmDeleteAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmStopAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmStartAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmSnapshotCreateAsync(vm, vmSnapshot, Guid.NewGuid(), "vm-x", "t-1");
        await queue.EnqueueVmSnapshotRevertAsync(vm, vmSnapshot, Guid.NewGuid(), "vm-x/s-x", "t-1");
        await queue.EnqueueVmSnapshotDeleteAsync(vm, vmSnapshot, Guid.NewGuid(), "vm-x/s-x", "t-1");
        await queue.EnqueueVmKeyAdditionCreateAsync(keyAdditionVm, keyAddition, Guid.NewGuid(), "vm-x", "t-1", ["ssh-ed25519 AAAA"]);
        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", new DateOnly(2026, 11, 11));
        await queue.EnqueueNetworkCreateAsync(network, Guid.NewGuid(), "t-1", null);
        await queue.EnqueueNetworkDeleteAsync(network, Guid.NewGuid(), "net-x", "t-1");
        await queue.EnqueueFirewallRuleCreateAsync(network, rule, Guid.NewGuid(), "t-1");
        await queue.EnqueueFirewallRuleDeleteAsync(network, rule, Guid.NewGuid(), "acl-x/rule", "t-1");
        await queue.EnqueueVolumeCreateAsync(volume, Guid.NewGuid(), "t-1");
        await queue.EnqueueVolumeAttachAsync(volume, Guid.NewGuid(), "vol-x", "vm-x", "t-1");
        await queue.EnqueueVolumeDetachAsync(volume, Guid.NewGuid(), "vol-x", "t-1");
        await queue.EnqueueVolumeDeleteAsync(volume, Guid.NewGuid(), "vol-x", "t-1");
        await queue.EnqueueVolumeSnapshotCreateAsync(volume, volumeSnapshot, Guid.NewGuid(), "vol-x", "t-1");
        await queue.EnqueueVolumeSnapshotDeleteAsync(volume, volumeSnapshot, Guid.NewGuid(), "vol-x/s-x", "t-1");
        await queue.EnqueueVolumeSnapshotRestoreAsync(volume, restored, Guid.NewGuid(), "vol-x/s-x", "t-1");
        await queue.EnqueueVolumeBackupAsync(volume, "vol-x", "t-1", new DateOnly(2026, 11, 11));
        await queue.EnqueueLoadBalancerCreateAsync(lb, Guid.NewGuid(), "t-1");
        await queue.EnqueueLoadBalancerUpdateAsync(lb, Guid.NewGuid(), "t-1");
        await queue.EnqueueLoadBalancerDeleteAsync(lb, Guid.NewGuid(), lb.Vip, "t-1");
        await queue.EnqueueTemplateSyncAsync(ATemplate(), "mock", Guid.NewGuid());
        await queue.EnqueueFunctionRuntimeSyncAsync(ARuntime(), "fn-dev", Guid.NewGuid());
        await queue.EnqueueFunctionDeployAsync(function, version, Guid.NewGuid());
        await queue.EnqueueFunctionDeleteAsync(function, Guid.NewGuid());
        await queue.EnqueueClusterBackupAsync("mock", new DateOnly(2026, 11, 11));
        await queue.EnqueueRestoreDrillAsync("mock", new DateOnly(2026, 11, 11), "vm", Guid.NewGuid(), "vm-x", "web-1", "t-1", Guid.NewGuid());
        await queue.EnqueueVmRestoreAsync(vm, restoredVm, Guid.NewGuid(), ArchiveKey, "t-1", []);
        await queue.EnqueueVolumeRestoreAsync(volume, restoredVolume, Guid.NewGuid(), ArchiveKey, "t-1");

        Assert.Equal(expected.Count, _repo.Rows.Count);
        Assert.All(_repo.Rows, row => Assert.Equal(expected[row.Kind], row.ResourceId));
    }

    [Fact]
    public async Task VmCreate_StagesOnePendingRow_KeyedOnTheRequest_WithTheWorkersPayload()
    {
        Vm vm = AVm();
        Guid requestId = Guid.NewGuid();

        await Queue().EnqueueVmCreateAsync(vm, requestId, Medium, "tpl-a", "net-1", "t-0123", vendorData: null);

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"vm-create-{vm.Id}-{requestId}", row.Key);
        Assert.Equal(ProviderCommandKinds.VmCreate, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(vm.Id.ToString(), payload.RootElement.GetProperty("vmId").GetString());
        Assert.Equal(requestId.ToString(), payload.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("t-0123", payload.RootElement.GetProperty("projectName").GetString());
        Assert.Equal("tpl-a", payload.RootElement.GetProperty("templateId").GetString());
        Assert.Equal(4096, payload.RootElement.GetProperty("memoryMb").GetInt32());
        Assert.Equal("web-1", payload.RootElement.GetProperty("name").GetString());
        Assert.Equal("net-1", payload.RootElement.GetProperty("externalNetworkId").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("vcpuCount").GetInt32());
        Assert.Equal(20, payload.RootElement.GetProperty("diskSizeGb").GetInt32());
        Assert.Equal("#cloud-config\n", payload.RootElement.GetProperty("userData").GetString());
        Assert.False(payload.RootElement.TryGetProperty("clusterId", out _));
    }

    // The worker reads the field as a string, empty meaning "none" (OpenViCloud/titan-incus-worker:resource/vm/create.go).
    [Theory]
    [InlineData("#!/bin/sh\necho hi\n", "#!/bin/sh\necho hi\n")]
    [InlineData(null, "")]
    public async Task VmCreate_CarriesTheVendorData_OrAnEmptyOne(string? vendorData, string expected)
    {
        await Queue().EnqueueVmCreateAsync(AVm(userData: null), Guid.NewGuid(), Medium, "tpl-a", "net-1", "t-0123", vendorData);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(expected, payload.RootElement.GetProperty("vendorData").GetString());
        Assert.Equal("", payload.RootElement.GetProperty("userData").GetString());
    }

    // One row per request (titan-ops architecture/resource-lifecycle.md#one-key-per-request): a second delete of the same VM is
    // a new request with its own key, never a repeat of the first row.
    [Fact]
    public async Task VmDelete_EachRequest_GetsItsOwnRow_CarryingItsRequestId()
    {
        Vm vm = AVm();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmDeleteAsync(vm, first, "vm-x", "t-1");
        await queue.EnqueueVmDeleteAsync(vm, second, "vm-x", "t-1");

        Assert.Equal(2, _repo.Rows.Count);
        Assert.Equal($"vm-delete-{vm.Id}-{first}", _repo.Rows[0].Key);
        Assert.Equal($"vm-delete-{vm.Id}-{second}", _repo.Rows[1].Key);
        Assert.Contains(second.ToString(), _repo.Rows[1].Payload);
    }

    // One rule for every key (titan-ops architecture/resource-lifecycle.md#one-key-per-request): a key that already has a
    // row is absorbed while the row is open or Done, and reopened when it is Failed. A resource's key carries its request
    // id, so only a resent request meets its own row; a backup's key is the day's, so every hourly ask does.
    public static TheoryData<string> KeyShapes() => new() { "resource", "backup" };

    private Task EnqueueTheSameKey(ProviderCommandQueue queue, string shape, Guid resourceId, Guid requestId, string externalId)
    {
        if (shape == "resource")
        {
            return queue.EnqueueVmDeleteAsync(_vmOfTheSameKey, requestId, externalId, "t-1");
        }
        return queue.EnqueueVmBackupAsync(_vmOfTheSameKey, externalId, "t-1", new DateOnly(2026, 10, 26));
    }

    private static ProviderCommand Finish(ProviderCommand row, ProviderCommandStatus status, DateTimeOffset now)
    {
        Guid lease = Guid.NewGuid();
        row.Lease(lease, now);
        if (status == ProviderCommandStatus.Leased)
        {
            return row;
        }
        if (status == ProviderCommandStatus.Done)
        {
            row.Complete(lease);
            return row;
        }
        row.Fail(lease, "gave up", retryable: false, now);
        return row;
    }

    [Theory]
    [MemberData(nameof(KeyShapes))]
    public async Task ARepeatOfAKey_OnItsOpenRow_IsAbsorbed(string shape)
    {
        Guid resourceId = Guid.NewGuid(), requestId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "first");
        ProviderCommand pending = Assert.Single(_repo.Rows);
        string payload = pending.Payload;

        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "second");
        Assert.Equal((ProviderCommandStatus.Pending, payload), (Assert.Single(_repo.Rows).Status, pending.Payload));

        Finish(pending, ProviderCommandStatus.Leased, _clock.GetUtcNow());
        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "third");
        Assert.Equal((ProviderCommandStatus.Leased, 1, payload), (pending.Status, pending.Attempts, pending.Payload));
    }

    [Theory]
    [MemberData(nameof(KeyShapes))]
    public async Task ARepeatOfAKey_OnItsDoneRow_IsAbsorbed(string shape)
    {
        Guid resourceId = Guid.NewGuid(), requestId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "first");
        ProviderCommand row = Finish(Assert.Single(_repo.Rows), ProviderCommandStatus.Done, _clock.GetUtcNow());
        string payload = row.Payload;

        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "second");

        Assert.Same(row, Assert.Single(_repo.Rows));
        Assert.Equal((ProviderCommandStatus.Done, payload), (row.Status, row.Payload));
    }

    [Theory]
    [MemberData(nameof(KeyShapes))]
    public async Task ARepeatOfAKey_OnItsFailedRow_ReopensIt_WithTheNewPayload(string shape)
    {
        Guid resourceId = Guid.NewGuid(), requestId = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();
        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "first");
        ProviderCommand row = Finish(Assert.Single(_repo.Rows), ProviderCommandStatus.Failed, _clock.GetUtcNow());

        await EnqueueTheSameKey(queue, shape, resourceId, requestId, "second");

        Assert.Same(row, Assert.Single(_repo.Rows));
        Assert.Equal((ProviderCommandStatus.Pending, 0), (row.Status, row.Attempts));
        Assert.Contains("second", row.Payload);
    }

    // A load balancer's keys carry the request id, as the network's: each request its own row, and the payload names the
    // request for the worker's report (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task LoadBalancerRequests_EachGetTheirOwnRow_CarryingTheirRequestId()
    {
        LoadBalancer lb = ALoadBalancer();
        Guid create = Guid.NewGuid();
        Guid update = Guid.NewGuid();
        Guid delete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueLoadBalancerCreateAsync(lb, create, "t-1");
        await queue.EnqueueLoadBalancerUpdateAsync(lb, update, "t-1");
        await queue.EnqueueLoadBalancerDeleteAsync(lb, delete, lb.Vip, "t-1");

        Assert.Equal(
            [
                $"load-balancer-create-{lb.Id}-{create}", $"load-balancer-update-{lb.Id}-{update}",
                $"load-balancer-delete-{lb.Id}-{delete}",
            ],
            _repo.Rows.Select(r => r.Key));
        Assert.Equal(
            [ProviderCommandKinds.LoadBalancerCreate, ProviderCommandKinds.LoadBalancerUpdate, ProviderCommandKinds.LoadBalancerDelete],
            _repo.Rows.Select(r => r.Kind));
        Guid[] requests = [create, update, delete];
        for (int i = 0; i < requests.Length; i++)
        {
            using JsonDocument payload = JsonDocument.Parse(_repo.Rows[i].Payload);
            Assert.Equal(requests[i].ToString(), payload.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(lb.Id.ToString(), payload.RootElement.GetProperty("loadBalancerId").GetString());
            Assert.False(payload.RootElement.TryGetProperty("intentId", out _));
        }
    }

    // Two deletes under two request ids while the first row is open: two rows, never a 409 on the load balancer's key; a
    // repeat while one waits never reaches the queue, the handler answers it.
    [Fact]
    public async Task LoadBalancerDelete_TwoRequestsWhileTheFirstIsOpen_EachGetTheirOwnPendingRow()
    {
        LoadBalancer lb = ALoadBalancer();
        Guid lbId = lb.Id;
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueLoadBalancerDeleteAsync(lb, first, lb.Vip, "t-1");
        await queue.EnqueueLoadBalancerDeleteAsync(lb, second, lb.Vip, "t-1");

        Assert.Equal([$"load-balancer-delete-{lbId}-{first}", $"load-balancer-delete-{lbId}-{second}"], _repo.Rows.Select(r => r.Key));
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandStatus.Pending, r.Status));
    }

    private static LoadBalancer ALoadBalancer(string clusterId = "cluster-a")
    {
        const string subnet = "2001:db8:0:1::/64";
        Guid tenantId = TestTenant.WithRoomToSpare().Id;
        Guid networkId = Guid.NewGuid();
        Vm vm = Vm.Create(tenantId, networkId, "vm-q", "m", "tpl", clusterId, 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vmq", VmStatus.Running, "10.0.0.5", null);
        return LoadBalancer.Create(tenantId, "web", networkId, "net-x", subnet, clusterId, LoadBalancerVip.For(subnet, 1),
            [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [vm]);
    }

    // The VM's keys carry the request id, so stop->start->stop are three rows; no handler enqueues a key twice
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task VmStopAndStart_AreKeyedOnTheirRequest()
    {
        Vm vm = AVm();
        Guid stop = Guid.NewGuid();
        Guid start = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmStopAsync(vm, stop, "vm-x", "t-1");
        await queue.EnqueueVmStartAsync(vm, start, "vm-x", "t-1");

        Assert.Equal($"vm-stop-{vm.Id}-{stop}", _repo.Rows[0].Key);
        Assert.Equal($"vm-start-{vm.Id}-{start}", _repo.Rows[1].Key);
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandStatus.Pending, r.Status));
    }

    // A volume's and a snapshot's keys carry the request id, as the VM's: each request its own row, and the payload names
    // the request for the worker's report (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task VolumeAndSnapshotRequests_EachGetTheirOwnRow_CarryingTheirRequestId()
    {
        (Volume volume, VolumeSnapshot snapshot) = AVolumeSnapshot();
        Guid volumeId = volume.Id;
        Guid snapshotId = snapshot.Id;
        Guid firstAttach = Guid.NewGuid();
        Guid secondAttach = Guid.NewGuid();
        Guid detach = Guid.NewGuid();
        Guid delete = Guid.NewGuid();
        Guid snapshotCreate = Guid.NewGuid();
        Guid snapshotDelete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVolumeAttachAsync(volume, firstAttach, "vol-x", "vm-x", "t-1");
        await queue.EnqueueVolumeAttachAsync(volume, secondAttach, "vol-x", "vm-y", "t-1");
        await queue.EnqueueVolumeDetachAsync(volume, detach, "vol-x", "t-1");
        await queue.EnqueueVolumeDeleteAsync(volume, delete, "vol-x", "t-1");
        await queue.EnqueueVolumeSnapshotCreateAsync(volume, snapshot, snapshotCreate, "vol-x", "t-1");
        await queue.EnqueueVolumeSnapshotDeleteAsync(volume, snapshot, snapshotDelete, "vol-x/s-x", "t-1");

        Assert.Equal(
            [
                $"volume-attach-{volumeId}-{firstAttach}", $"volume-attach-{volumeId}-{secondAttach}",
                $"volume-detach-{volumeId}-{detach}", $"volume-delete-{volumeId}-{delete}",
                $"volume-snapshot-create-{snapshotId}-{snapshotCreate}", $"volume-snapshot-delete-{snapshotId}-{snapshotDelete}",
            ],
            _repo.Rows.Select(r => r.Key));
        Guid[] requests = [firstAttach, secondAttach, detach, delete, snapshotCreate, snapshotDelete];
        for (int i = 0; i < requests.Length; i++)
        {
            using JsonDocument payload = JsonDocument.Parse(_repo.Rows[i].Payload);
            Assert.Equal(requests[i].ToString(), payload.RootElement.GetProperty("requestId").GetString());
        }
    }

    // The worker's report names the request (titan-ops architecture/resource-lifecycle.md#callback-routes): the id travels in the
    // payload as requestId, under no other name.
    [Fact]
    public async Task StopAndStart_CarryTheRequestId_AsRequestIdOnly()
    {
        Vm vm = AVm();
        Guid stopRequest = Guid.NewGuid();
        Guid startRequest = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmStopAsync(vm, stopRequest, "vm-x", "t-1");
        await queue.EnqueueVmStartAsync(vm, startRequest, "vm-x", "t-1");

        using JsonDocument stop = JsonDocument.Parse(_repo.Rows[0].Payload);
        Assert.Equal(stopRequest.ToString(), stop.RootElement.GetProperty("requestId").GetString());
        Assert.False(stop.RootElement.TryGetProperty("intentId", out _));
        using JsonDocument start = JsonDocument.Parse(_repo.Rows[1].Payload);
        Assert.Equal(startRequest.ToString(), start.RootElement.GetProperty("requestId").GetString());
        Assert.False(start.RootElement.TryGetProperty("intentId", out _));
        Assert.Equal($"vm-start-{vm.Id}-{startRequest}", _repo.Rows[1].Key);
    }

    // The worker waits this long for the guest to shut down before it cuts the power
    // (titan-ops features/vm/design.md#vm-stop-timeout): the VM's value when the stop is recorded.
    [Fact]
    public async Task VmStop_CarriesTheVmsStopTimeout()
    {
        Vm vm = Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "db-1", "m", "tpl-guid", "mock", 20, stopTimeoutSeconds: 1800);

        await Queue().EnqueueVmStopAsync(vm, Guid.NewGuid(), "vm-x", "t-1");
        vm.ChangeStopTimeout(60);

        using JsonDocument stop = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(1800, stop.RootElement.GetProperty("stopTimeoutSeconds").GetInt32());
    }

    // A network's and a rule's keys carry the request id, as the volume's: each request its own row, and the payload names
    // the request for the worker's report (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task NetworkAndFirewallRuleRequests_EachGetTheirOwnRow_CarryingTheirRequestId()
    {
        (Network network, FirewallRule rule) = ARule();
        Guid networkId = network.Id;
        Guid ruleId = rule.Id;
        Guid networkCreate = Guid.NewGuid();
        Guid networkDelete = Guid.NewGuid();
        Guid ruleCreate = Guid.NewGuid();
        Guid ruleDelete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueNetworkCreateAsync(network, networkCreate, "t-1", null);
        await queue.EnqueueNetworkDeleteAsync(network, networkDelete, "net-x", "t-1");
        await queue.EnqueueFirewallRuleCreateAsync(network, rule, ruleCreate, "t-1");
        await queue.EnqueueFirewallRuleDeleteAsync(network, rule, ruleDelete, "acl-x/rule", "t-1");

        Assert.Equal(
            [
                $"network-create-{networkId}-{networkCreate}", $"network-delete-{networkId}-{networkDelete}",
                $"firewall-rule-create-{ruleId}-{ruleCreate}", $"firewall-rule-delete-{ruleId}-{ruleDelete}",
            ],
            _repo.Rows.Select(r => r.Key));
        Assert.Equal(
            [ProviderCommandKinds.NetworkCreate, ProviderCommandKinds.NetworkDelete,
                ProviderCommandKinds.FirewallRuleCreate, ProviderCommandKinds.FirewallRuleDelete],
            _repo.Rows.Select(r => r.Kind));
        Guid[] requests = [networkCreate, networkDelete, ruleCreate, ruleDelete];
        for (int i = 0; i < requests.Length; i++)
        {
            using JsonDocument payload = JsonDocument.Parse(_repo.Rows[i].Payload);
            Assert.Equal(requests[i].ToString(), payload.RootElement.GetProperty("requestId").GetString());
        }
    }

    // A second delete of the same network or rule is a new request with its own row, never a 409 on the first's open row
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task NetworkAndFirewallRuleDelete_TwoRequestsWhileTheFirstIsOpen_EachGetTheirOwnPendingRow()
    {
        (Network network, FirewallRule rule) = ARule();
        Guid networkId = network.Id;
        Guid ruleId = rule.Id;
        Guid[] requests = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueNetworkDeleteAsync(network, requests[0], "net-x", "t-1");
        await queue.EnqueueNetworkDeleteAsync(network, requests[1], "net-x", "t-1");
        await queue.EnqueueFirewallRuleDeleteAsync(network, rule, requests[2], "acl-x/rule", "t-1");
        await queue.EnqueueFirewallRuleDeleteAsync(network, rule, requests[3], "acl-x/rule", "t-1");

        Assert.Equal(
            [
                $"network-delete-{networkId}-{requests[0]}", $"network-delete-{networkId}-{requests[1]}",
                $"firewall-rule-delete-{ruleId}-{requests[2]}", $"firewall-rule-delete-{ruleId}-{requests[3]}",
            ],
            _repo.Rows.Select(r => r.Key));
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandStatus.Pending, r.Status));
    }

    // A VM snapshot's keys carry the request id, as the network's: each request its own row, and the payload names the
    // request for the worker's report (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task VmSnapshotRequests_EachGetTheirOwnRow_CarryingTheirRequestId()
    {
        (Vm vm, VmSnapshot snapshot) = AVmSnapshot();
        Guid snapshotId = snapshot.Id;
        Guid vmId = vm.Id;
        Guid create = Guid.NewGuid();
        Guid revert = Guid.NewGuid();
        Guid delete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmSnapshotCreateAsync(vm, snapshot, create, "vm-x", "t-1");
        await queue.EnqueueVmSnapshotRevertAsync(vm, snapshot, revert, "vm-x/s-x", "t-1");
        await queue.EnqueueVmSnapshotDeleteAsync(vm, snapshot, delete, "vm-x/s-x", "t-1");

        Assert.Equal(
            [
                $"vm-snapshot-create-{snapshotId}-{create}", $"vm-snapshot-revert-{snapshotId}-{revert}",
                $"vm-snapshot-delete-{snapshotId}-{delete}",
            ],
            _repo.Rows.Select(r => r.Key));
        Assert.Equal(
            [ProviderCommandKinds.VmSnapshotCreate, ProviderCommandKinds.VmSnapshotRevert, ProviderCommandKinds.VmSnapshotDelete],
            _repo.Rows.Select(r => r.Kind));
        Guid[] requests = [create, revert, delete];
        for (int i = 0; i < requests.Length; i++)
        {
            using JsonDocument payload = JsonDocument.Parse(_repo.Rows[i].Payload);
            Assert.Equal(requests[i].ToString(), payload.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(vmId.ToString(), payload.RootElement.GetProperty("vmId").GetString());
        }
    }

    // At the queue, a revert or delete under a new request id gets its own row, never a 409 on an open row of the same
    // snapshot (titan-ops architecture/resource-lifecycle.md#one-key-per-request); a repeat while one waits never reaches
    // the queue: the handler answers it.
    [Fact]
    public async Task VmSnapshotRevertAndDelete_TwoRequestsWhileTheFirstIsOpen_EachGetTheirOwnPendingRow()
    {
        (Vm vm, VmSnapshot snapshot) = AVmSnapshot();
        Guid snapshotId = snapshot.Id;
        Guid[] requests = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmSnapshotRevertAsync(vm, snapshot, requests[0], "vm-x/s-x", "t-1");
        await queue.EnqueueVmSnapshotRevertAsync(vm, snapshot, requests[1], "vm-x/s-x", "t-1");
        await queue.EnqueueVmSnapshotDeleteAsync(vm, snapshot, requests[2], "vm-x/s-x", "t-1");
        await queue.EnqueueVmSnapshotDeleteAsync(vm, snapshot, requests[3], "vm-x/s-x", "t-1");

        Assert.Equal(
            [
                $"vm-snapshot-revert-{snapshotId}-{requests[0]}", $"vm-snapshot-revert-{snapshotId}-{requests[1]}",
                $"vm-snapshot-delete-{snapshotId}-{requests[2]}", $"vm-snapshot-delete-{snapshotId}-{requests[3]}",
            ],
            _repo.Rows.Select(r => r.Key));
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandStatus.Pending, r.Status));
    }

    // The delete callback URL is /internal/networks/{networkId}/firewall-rules/{ruleId}/requests/{requestId}.
    [Fact]
    public async Task FirewallRuleDelete_CarriesTheNetworkIdTheCallbackNeeds()
    {
        (Network network, FirewallRule rule) = ARule();
        Guid networkId = network.Id;

        await Queue().EnqueueFirewallRuleDeleteAsync(network, rule, Guid.NewGuid(), "acl-1", "t-1");

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(networkId.ToString(), payload.RootElement.GetProperty("networkId").GetString());
    }

    // The worker merges the two policies into the network's default action
    // (titan-ops features/network/design.md#firewall-default-policy): it needs both.
    [Fact]
    public async Task NetworkCreate_CarriesBothPolicies()
    {
        await Queue().EnqueueNetworkCreateAsync(ANetwork(ingress: FirewallPolicy.Deny, cidr: "10.40.0.0/24"), Guid.NewGuid(), "t-1", null);

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("Deny", payload.RootElement.GetProperty("defaultIngressPolicy").GetString());
        Assert.Equal("Allow", payload.RootElement.GetProperty("defaultEgressPolicy").GetString());
    }

    // The worker reads both as strings, empty meaning "no public IPv6" (OpenViCloud/titan-incus-worker:resource/network/create.go),
    // and builds the network on the uplink with the router at that address (titan-ops infra/incus/design.md#ipv6-address-plan).
    [Theory]
    [InlineData("2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170", "2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170")]
    [InlineData(null, null, "", "")]
    public async Task NetworkCreate_CarriesThePublicIpv6_OrEmptyStrings(string? subnet, string? router, string wantSubnet, string wantRouter)
    {
        Network network = ANetwork("singapore01");
        if (subnet != null)
        {
            network.ReceivePublicIpv6(subnet);
        }

        await Queue().EnqueueNetworkCreateAsync(network, Guid.NewGuid(), "t-1", router);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(wantSubnet, payload.RootElement.GetProperty("publicIpv6Subnet").GetString());
        Assert.Equal(wantRouter, payload.RootElement.GetProperty("publicIpv6RouterAddress").GetString());
    }

    // An old worker would read the removed field; the new payload must not carry it.
    [Fact]
    public async Task VmCreate_NoLongerCarriesEnablePublicIpv6()
    {
        await Queue().EnqueueVmCreateAsync(AVm(), Guid.NewGuid(), Medium, "tpl-a", "net-1", "t-0123", null);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.False(payload.RootElement.TryGetProperty("enablePublicIpv6", out _));
    }

    // The worker never read it (OpenViCloud/titan-incus-worker:resource/vm/create.go): the sizing travels as three numbers
    // (titan-ops features/catalog/design.md#catalog-resolution-at-create).
    [Fact]
    public async Task VmCreate_CarriesNoVmOfferingId()
    {
        await Queue().EnqueueVmCreateAsync(AVm(), Guid.NewGuid(), Medium, "tpl-a", "net-1", "t-0123", null);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.False(payload.RootElement.TryGetProperty("vmOfferingId", out _));
    }

    // A rule lives in its network's ACL, inside the tenant's project (titan-ops features/network/design.md#firewall-on-incus).
    [Fact]
    public async Task FirewallRuleCreateAndDelete_CarryTheProject()
    {
        ProviderCommandQueue queue = Queue();
        (Network network, FirewallRule rule) = ARule();

        await queue.EnqueueFirewallRuleCreateAsync(network, rule, Guid.NewGuid(), "t-1");
        await queue.EnqueueFirewallRuleDeleteAsync(network, rule, Guid.NewGuid(), "acl-x/rule", "t-1");

        foreach (ProviderCommand row in _repo.Rows)
        {
            using JsonDocument payload = JsonDocument.Parse(row.Payload);
            Assert.Equal("t-1", payload.RootElement.GetProperty("projectName").GetString());
            Assert.Equal("mock", row.ClusterId);
        }
        Assert.Equal(2, _repo.Rows.Count);
    }

    // A restore is the new volume's create: keyed like one, so the stuck-request query finds it; keyed on the snapshot, a
    // second restore would be absorbed by the first's open row.
    [Fact]
    public async Task VolumeRestore_IsKeyedAsTheNewVolumesCreate_NotOnTheSnapshot()
    {
        Volume firstVolume = AVolume();
        Volume secondVolume = AVolume();
        Guid secondRequest = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVolumeSnapshotRestoreAsync(AVolume(), firstVolume, Guid.NewGuid(), "vol-src/snap-1", "t-1");
        await queue.EnqueueVolumeSnapshotRestoreAsync(AVolume(), secondVolume, secondRequest, "vol-src/snap-1", "t-1");

        Assert.Equal(2, _repo.Rows.Count);
        Assert.Equal($"volume-create-{secondVolume.Id}-{secondRequest}", _repo.Rows[1].Key);
        Assert.Equal(ProviderCommandKinds.VolumeSnapshotRestore, _repo.Rows[1].Kind);
    }

    // An admin's restore of a backup archive (titan-ops features/backup/design.md#restore-as-new-resource) is the new
    // volume's create too: keyed like one, on the source's cluster. Its row carries no tenant, as the drill's, and holds
    // the new volume.
    [Fact]
    public async Task VolumeRestoreFromABackup_IsTheNewVolumesCreate_OfNoTenant_OnTheSourcesCluster()
    {
        (Volume source, Volume copy) = ARestoredVolume("singapore01");
        Guid requestId = copy.Operation.PendingId!.Value;

        await Queue().EnqueueVolumeRestoreAsync(source, copy, requestId, ArchiveKey, "t-0123");

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"volume-create-{copy.Id}-{requestId}", row.Key);
        Assert.Equal(ProviderCommandKinds.VolumeRestore, row.Kind);
        Assert.Equal("singapore01", row.ClusterId);
        Assert.Null(row.TenantId);
        Assert.Equal(copy.Id, row.ResourceId);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        JsonElement sent = payload.RootElement;
        Assert.Equal(copy.Id, sent.GetProperty("newVolumeId").GetGuid());
        Assert.Equal(requestId, sent.GetProperty("requestId").GetGuid());
        Assert.Equal("data-1-restored", sent.GetProperty("name").GetString());
        Assert.Equal(source.Id, sent.GetProperty("sourceVolumeId").GetGuid());
        Assert.Equal(ArchiveKey, sent.GetProperty("backupKey").GetString());
        Assert.Equal(source.TenantId.ToString(), sent.GetProperty("tenantId").GetString());
        Assert.Equal("t-0123", sent.GetProperty("projectName").GetString());
        Assert.Equal(50, sent.GetProperty("sizeGb").GetInt32());
    }

    [Fact]
    public async Task VmRestoreFromABackup_IsTheNewVmsCreate_OfNoTenant_OnTheSourcesCluster()
    {
        (Vm source, Vm copy) = ARestoredVm("singapore01");
        Guid requestId = copy.Operation.PendingId!.Value;

        await Queue().EnqueueVmRestoreAsync(source, copy, requestId, ArchiveKey, "t-0123", []);

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"vm-create-{copy.Id}-{requestId}", row.Key);
        Assert.Equal(ProviderCommandKinds.VmRestore, row.Kind);
        Assert.Equal("singapore01", row.ClusterId);
        Assert.Null(row.TenantId);
        Assert.Equal(copy.Id, row.ResourceId);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        JsonElement sent = payload.RootElement;
        Assert.Equal(copy.Id, sent.GetProperty("vmId").GetGuid());
        Assert.Equal(requestId, sent.GetProperty("requestId").GetGuid());
        Assert.Equal("web-1-restored", sent.GetProperty("name").GetString());
        Assert.Equal(source.Id, sent.GetProperty("sourceVmId").GetGuid());
        Assert.Equal(ArchiveKey, sent.GetProperty("backupKey").GetString());
        Assert.Equal(source.TenantId.ToString(), sent.GetProperty("tenantId").GetString());
        Assert.Equal("t-0123", sent.GetProperty("projectName").GetString());
        Assert.Equal(20, sent.GetProperty("diskSizeGb").GetInt32());
        // No volume goes with it: an empty list, not an absent field.
        Assert.Equal(JsonValueKind.Array, sent.GetProperty("volumes").ValueKind);
        Assert.Equal(0, sent.GetProperty("volumes").GetArrayLength());
    }

    // Each volume restored with the VM is named by its source, its copy and the id of the copy's own create, in the order
    // given, and by nothing else.
    [Fact]
    public async Task VmRestoreFromABackup_NamesEachVolumeCopy_WithItsOwnRequest()
    {
        (Vm source, Vm copy) = ARestoredVm();
        var first = new VolumeRestoredWithItsVm(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = new VolumeRestoredWithItsVm(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Guid vmRequest = Guid.NewGuid();

        await Queue().EnqueueVmRestoreAsync(source, copy, vmRequest, ArchiveKey, "t-0123", [first, second]);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(vmRequest, payload.RootElement.GetProperty("requestId").GetGuid());
        List<JsonElement> volumes = payload.RootElement.GetProperty("volumes").EnumerateArray().ToList();
        Assert.Equal(2, volumes.Count);
        foreach ((JsonElement sent, VolumeRestoredWithItsVm expected) in volumes.Zip([first, second]))
        {
            Assert.Equal(["newVolumeId", "requestId", "sourceVolumeId"],
                sent.EnumerateObject().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(expected.SourceVolumeId, sent.GetProperty("sourceVolumeId").GetGuid());
            Assert.Equal(expected.NewVolumeId, sent.GetProperty("newVolumeId").GetGuid());
            Assert.Equal(expected.RequestId, sent.GetProperty("requestId").GetGuid());
        }
    }

    // The worker rebuilds the archive's object key from the payload, tenant included, and it must be the key the backup
    // wrote: the tenant travels in the restore exactly as it does in the backup.
    [Fact]
    public async Task ARestoreFromABackup_WritesTheTenant_AsTheBackupDoes()
    {
        (Vm vm, Vm vmCopy) = ARestoredVm();
        (Volume volume, Volume volumeCopy) = ARestoredVolume();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", new DateOnly(2026, 10, 26));
        await queue.EnqueueVmRestoreAsync(vm, vmCopy, Guid.NewGuid(), ArchiveKey, "t-1", []);
        await queue.EnqueueVolumeBackupAsync(volume, "vol-x", "t-1", new DateOnly(2026, 10, 26));
        await queue.EnqueueVolumeRestoreAsync(volume, volumeCopy, Guid.NewGuid(), ArchiveKey, "t-1");

        string[] tenants = _repo.Rows
            .Select(r => { using JsonDocument payload = JsonDocument.Parse(r.Payload); return payload.RootElement.GetProperty("tenantId").GetRawText(); })
            .ToArray();
        Assert.Equal(4, tenants.Length);
        Assert.Equal((tenants[0], tenants[2]), (tenants[1], tenants[3]));
        Assert.Equal($"\"{vm.TenantId}\"", tenants[1]);
    }

    // An Incus image carries no CPU/RAM: sizing travels with the VM (titan-ops features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task TemplateSync_CarriesTheImageAlias_AndNoSizing()
    {
        Guid requestId = Guid.NewGuid();
        await Queue().EnqueueTemplateSyncAsync(ATemplate(), "mock", requestId);

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("almalinux/9", payload.RootElement.GetProperty("osImageName").GetString());
        Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetGuid());
        Assert.False(payload.RootElement.TryGetProperty("memoryMb", out _));
        Assert.False(payload.RootElement.TryGetProperty("vcpuCount", out _));
    }

    // The worker imports the distro's own image when the template names one; none ⇒ "" and it pulls osImageName
    // (titan-ops features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task TemplateSync_CarriesTheImageUrl_OrAnEmptyOne()
    {
        const string url = "https://cloud-images.ubuntu.com/releases/jammy/release/ubuntu-22.04-server-cloudimg-{arch}.img";
        await Queue().EnqueueTemplateSyncAsync(ATemplate(url), "mock", Guid.NewGuid());
        await Queue().EnqueueTemplateSyncAsync(ATemplate(), "mock", Guid.NewGuid());

        Assert.Equal(2, _repo.Rows.Count);
        using JsonDocument withUrl = JsonDocument.Parse(_repo.Rows[0].Payload);
        using JsonDocument withoutUrl = JsonDocument.Parse(_repo.Rows[1].Payload);
        Assert.Equal(url, withUrl.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("", withoutUrl.RootElement.GetProperty("imageUrl").GetString());
    }

    // One row per sync request of one cluster (titan-ops architecture/resource-lifecycle.md#one-key-per-request): the two
    // clusters of one sync-all, and a later sync of the same cluster, each get their own.
    [Fact]
    public async Task TemplateSync_IsKeyedPerTemplatePerClusterPerRequest()
    {
        VmTemplate template = ATemplate();
        Guid templateId = template.Id;
        Guid inSingapore = Guid.NewGuid(), inMock = Guid.NewGuid(), inMockAgain = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueTemplateSyncAsync(template, "singapore01", inSingapore);
        await queue.EnqueueTemplateSyncAsync(template, "mock", inMock);
        await queue.EnqueueTemplateSyncAsync(template, "mock", inMockAgain);

        Assert.Equal(
            new[]
            {
                $"template-sync-{templateId}-singapore01-{inSingapore}", $"template-sync-{templateId}-mock-{inMock}",
                $"template-sync-{templateId}-mock-{inMockAgain}",
            },
            _repo.Rows.Select(r => r.Key).ToArray());
    }

    // One row per sync request of one function cluster, as a template's (titan-ops features/ovc-function/design.md#function-lifecycle):
    // the row carries the function cluster, which only its worker leases, and no tenant: the catalog is global.
    [Fact]
    public async Task FunctionRuntimeSync_IsKeyedPerRuntimePerClusterPerRequest_OnTheFunctionCluster()
    {
        FunctionRuntime runtime = ARuntime();
        Guid first = Guid.NewGuid(), again = Guid.NewGuid(), elsewhere = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueFunctionRuntimeSyncAsync(runtime, "fn-dev", first);
        await queue.EnqueueFunctionRuntimeSyncAsync(runtime, "fn-dev", again);
        await queue.EnqueueFunctionRuntimeSyncAsync(runtime, "fn-two", elsewhere);

        Assert.Equal(
            new[]
            {
                $"function-runtime-sync-{runtime.Id}-fn-dev-{first}", $"function-runtime-sync-{runtime.Id}-fn-dev-{again}",
                $"function-runtime-sync-{runtime.Id}-fn-two-{elsewhere}",
            },
            _repo.Rows.Select(r => r.Key).ToArray());
        Assert.Equal(["fn-dev", "fn-dev", "fn-two"], _repo.Rows.Select(r => r.ClusterId));
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandKinds.FunctionRuntimeSync, r.Kind));
        Assert.All(_repo.Rows, r => Assert.Null(r.TenantId));
        Assert.All(_repo.Rows, r => Assert.Null(r.ResourceId));
    }

    [Fact]
    public async Task FunctionRuntimeSync_CarriesTheRuntime_ItsImagePinnedByDigest_AndTheRequest()
    {
        FunctionRuntime runtime = ARuntime();
        Guid requestId = Guid.NewGuid();

        await Queue().EnqueueFunctionRuntimeSyncAsync(runtime, "fn-dev", requestId);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(runtime.Id, payload.RootElement.GetProperty("runtimeId").GetGuid());
        Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal("nodejs26", payload.RootElement.GetProperty("name").GetString());
        Assert.Equal(RuntimeImage, payload.RootElement.GetProperty("image").GetString());
    }

    // One row per deploy or delete request of a function, on its function cluster, in its tenant's lane, holding the
    // function (titan-ops features/ovc-function/design.md#function-lifecycle).
    [Fact]
    public async Task FunctionDeployAndDelete_AreKeyedPerFunctionPerRequest_OnItsCluster_ForItsTenant()
    {
        (Function function, FunctionVersion version) = AFunctionVersion();
        Guid deploy = Guid.NewGuid(), again = Guid.NewGuid(), delete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueFunctionDeployAsync(function, version, deploy);
        await queue.EnqueueFunctionDeployAsync(function, version, again);
        await queue.EnqueueFunctionDeleteAsync(function, delete);

        Assert.Equal(
            new[]
            {
                $"function-deploy-{function.Id}-{deploy}", $"function-deploy-{function.Id}-{again}",
                $"function-delete-{function.Id}-{delete}",
            },
            _repo.Rows.Select(r => r.Key).ToArray());
        Assert.Equal([ProviderCommandKinds.FunctionDeploy, ProviderCommandKinds.FunctionDeploy, ProviderCommandKinds.FunctionDelete],
            _repo.Rows.Select(r => r.Kind));
        Assert.All(_repo.Rows, r => Assert.Equal("fn-dev", r.ClusterId));
        Assert.All(_repo.Rows, r => Assert.Equal(function.TenantId, r.TenantId));
        Assert.All(_repo.Rows, r => Assert.Equal(function.Id, r.ResourceId));
    }

    // Every value the worker applies (titan-ops features/ovc-function/design.md#function-limits): the version's image,
    // handler and size, CPU in proportion to memory, and the tenant namespace's quota and limits.
    [Fact]
    public async Task FunctionDeploy_CarriesTheVersion_ItsSize_AndTheNamespaceLimits()
    {
        (Function function, FunctionVersion version) = AFunctionVersion();
        Guid requestId = Guid.NewGuid();

        await Queue().EnqueueFunctionDeployAsync(function, version, requestId);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        JsonElement root = payload.RootElement;
        Assert.Equal(function.Id, root.GetProperty("functionId").GetGuid());
        Assert.Equal(requestId, root.GetProperty("requestId").GetGuid());
        Assert.Equal(function.TenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(version.Id, root.GetProperty("versionId").GetGuid());
        Assert.Equal($"localhost:5100/fn/{function.TenantId}/{function.Id}@{TestFunctions.Digest}", root.GetProperty("image").GetString());
        Assert.Equal("main.handler", root.GetProperty("handler").GetString());
        Assert.Equal(256, root.GetProperty("memoryMiB").GetInt32());
        Assert.Equal("250m", root.GetProperty("cpu").GetString());
        Assert.Equal(30, root.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(10, root.GetProperty("maxInstances").GetInt32());
        Assert.Equal(1, root.GetProperty("concurrency").GetInt32());
        JsonElement quota = root.GetProperty("tenantQuota");
        Assert.Equal(["cronJobs", "memoryMiB", "pods", "services"], quota.EnumerateObject().Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(43520, quota.GetProperty("memoryMiB").GetInt32());
        Assert.Equal(20, quota.GetProperty("pods").GetInt32());
        Assert.Equal(20, quota.GetProperty("services").GetInt32());
        Assert.Equal(20, quota.GetProperty("cronJobs").GetInt32());
        JsonElement limits = root.GetProperty("containerLimits");
        Assert.Equal(["defaultCpu", "defaultMemoryMiB", "maxCpu", "maxMemoryMiB"],
            limits.EnumerateObject().Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(128, limits.GetProperty("defaultMemoryMiB").GetInt32());
        Assert.Equal(2048, limits.GetProperty("maxMemoryMiB").GetInt32());
        Assert.Equal("125m", limits.GetProperty("defaultCpu").GetString());
        Assert.Equal("2000m", limits.GetProperty("maxCpu").GetString());
    }

    // Go's bootstrap registers its own handler: the field is there, empty.
    [Fact]
    public async Task FunctionDeploy_OfAGoVersion_CarriesAnEmptyHandler()
    {
        (Function function, FunctionVersion version) = AFunctionVersion(handler: null);

        await Queue().EnqueueFunctionDeployAsync(function, version, Guid.NewGuid());

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal("", payload.RootElement.GetProperty("handler").GetString());
    }

    [Fact]
    public async Task FunctionDelete_CarriesTheFunction_TheRequest_AndTheTenant()
    {
        Function function = AFunctionVersion().Function;
        Guid requestId = Guid.NewGuid();

        await Queue().EnqueueFunctionDeleteAsync(function, requestId);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(function.Id, payload.RootElement.GetProperty("functionId").GetGuid());
        Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal(function.TenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
    }

    // One backup a day per resource (titan-ops features/backup/design.md#incus-backup):
    // the UTC day is in the key, and the payload carries what the worker needs to name the object in the bucket.
    [Fact]
    public async Task VmBackup_KeyIsTheVmAndTheDay_PayloadCarriesTenantAndProject()
    {
        Vm vm = AVm();
        Guid vmId = vm.Id;
        Guid tenantId = vm.TenantId;

        await Queue().EnqueueVmBackupAsync(vm, "vm-x", "t-0123", new DateOnly(2026, 10, 26));

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
        Volume volume = AVolume();
        Guid volumeId = volume.Id;
        Guid tenantId = volume.TenantId;

        await Queue().EnqueueVolumeBackupAsync(volume, "vol-x", "t-0123", new DateOnly(2026, 10, 26));

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
        Vm vm = AVm();
        DateOnly day = new(2026, 10, 26);
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", day);
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Complete(lease);

        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", day);

        Assert.Equal(ProviderCommandStatus.Done, Assert.Single(_repo.Rows).Status);
    }

    [Fact]
    public async Task VmBackup_SameDayAfterItFailed_RunsAgain()
    {
        Vm vm = AVm();
        DateOnly day = new(2026, 10, 26);
        ProviderCommandQueue queue = Queue();
        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", day);
        ProviderCommand row = Assert.Single(_repo.Rows);
        Guid lease = Guid.NewGuid();
        row.Lease(lease, _clock.GetUtcNow());
        row.Fail(lease, "upload refused", retryable: false, _clock.GetUtcNow());

        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", day);

        Assert.Equal(ProviderCommandStatus.Pending, Assert.Single(_repo.Rows).Status);
    }

    [Fact]
    public async Task VmBackup_KeyHasTheGregorianDay_UnderABuddhistCalendarCulture()
    {
        Vm vm = AVm();
        Guid vmId = vm.Id;
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
        try
        {
            await Queue().EnqueueVmBackupAsync(vm, "vm-x", "t-1", new DateOnly(2026, 10, 26));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }

        Assert.Equal($"vm-backup-{vmId}-20261026", Assert.Single(_repo.Rows).Key);
    }

    // The config asked again the same day has a key of its own, one per UTC hour; the day's first keeps the old form
    // (titan-ops features/backup/design.md#cluster-backup).
    [Fact]
    public async Task ClusterBackupAgain_KeyHasTheDayAndTheHour_InGregorianDigits_UnderABuddhistCalendarCulture()
    {
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
        try
        {
            await Queue().EnqueueClusterBackupAsync("mock", new DateOnly(2026, 10, 26));
            await Queue().EnqueueClusterBackupAgainAsync("mock", new DateOnly(2026, 10, 26), 6);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }

        Assert.Equal(new[] { "cluster-backup-mock-20261026", "cluster-backup-mock-2026102606" }, _repo.Rows.Select(r => r.Key));
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandKinds.ClusterBackup, r.Kind));
        Assert.All(_repo.Rows, r => Assert.Null(r.ResourceId));
    }

    [Fact]
    public async Task VmBackup_NextDay_IsANewRow()
    {
        Vm vm = AVm();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", new DateOnly(2026, 10, 26));
        await queue.EnqueueVmBackupAsync(vm, "vm-x", "t-1", new DateOnly(2026, 10, 27));

        Assert.Equal(2, _repo.Rows.Count);
    }

    // Incus keeps volumes and snapshots inside the tenant's project: a storage or snapshot command that does not say
    // which project cannot be carried out (titan-ops architecture/provider-and-worker.md#translate-titan-ids).
    public static TheoryData<string> StorageAndSnapshotKinds() => new()
    {
        ProviderCommandKinds.VolumeCreate, ProviderCommandKinds.VolumeAttach, ProviderCommandKinds.VolumeDetach,
        ProviderCommandKinds.VolumeDelete, ProviderCommandKinds.VolumeSnapshotCreate,
        ProviderCommandKinds.VolumeSnapshotDelete, ProviderCommandKinds.VolumeSnapshotRestore,
        ProviderCommandKinds.VmSnapshotCreate, ProviderCommandKinds.VmSnapshotRevert, ProviderCommandKinds.VmSnapshotDelete,
        ProviderCommandKinds.VmBackup, ProviderCommandKinds.VolumeBackup,
        ProviderCommandKinds.VmRestore, ProviderCommandKinds.VolumeRestore,
    };

    [Theory]
    [MemberData(nameof(StorageAndSnapshotKinds))]
    public async Task StorageAndSnapshotCommands_CarryTheTenantsProject_AndTheirRowTheCluster(string kind)
    {
        await EnqueueOne(Queue(), kind, project: "t-0123");

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal(kind, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("t-0123", payload.RootElement.GetProperty("projectName").GetString());
    }

    // The worker has no catalog: an offering id is a Titan id the provider cannot use; the pool comes from the cluster.
    [Fact]
    public async Task VolumeCreate_CarriesTheSize_NotTheOfferingId()
    {
        await Queue().EnqueueVolumeCreateAsync(AVolume(), Guid.NewGuid(), "t-0123");

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(50, payload.RootElement.GetProperty("sizeGb").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("volumeOfferingId", out _));
    }

    private static Task EnqueueOne(ProviderCommandQueue queue, string kind, string project)
    {
        Guid request = Guid.NewGuid();
        switch (kind)
        {
            case ProviderCommandKinds.VolumeCreate:
                return queue.EnqueueVolumeCreateAsync(AVolume(), request, project);
            case ProviderCommandKinds.VolumeAttach:
                return queue.EnqueueVolumeAttachAsync(AVolume(), request, "vol-x", "vm-x", project);
            case ProviderCommandKinds.VolumeDetach:
                return queue.EnqueueVolumeDetachAsync(AVolume(), request, "vol-x", project);
            case ProviderCommandKinds.VolumeDelete:
                return queue.EnqueueVolumeDeleteAsync(AVolume(), request, "vol-x", project);
            case ProviderCommandKinds.VolumeSnapshotCreate:
            {
                (Volume volume, VolumeSnapshot snapshot) = AVolumeSnapshot();
                return queue.EnqueueVolumeSnapshotCreateAsync(volume, snapshot, request, "vol-x", project);
            }
            case ProviderCommandKinds.VolumeSnapshotDelete:
            {
                (Volume volume, VolumeSnapshot snapshot) = AVolumeSnapshot();
                return queue.EnqueueVolumeSnapshotDeleteAsync(volume, snapshot, request, "vol-x/snap-x", project);
            }
            case ProviderCommandKinds.VolumeSnapshotRestore:
                return queue.EnqueueVolumeSnapshotRestoreAsync(AVolume(), AVolume(), request, "vol-x/snap-x", project);
            case ProviderCommandKinds.VmSnapshotCreate:
            {
                (Vm vm, VmSnapshot snapshot) = AVmSnapshot();
                return queue.EnqueueVmSnapshotCreateAsync(vm, snapshot, request, "vm-x", project);
            }
            case ProviderCommandKinds.VmSnapshotRevert:
            {
                (Vm vm, VmSnapshot snapshot) = AVmSnapshot();
                return queue.EnqueueVmSnapshotRevertAsync(vm, snapshot, request, "vm-x/snap-x", project);
            }
            case ProviderCommandKinds.VmSnapshotDelete:
            {
                (Vm vm, VmSnapshot snapshot) = AVmSnapshot();
                return queue.EnqueueVmSnapshotDeleteAsync(vm, snapshot, request, "vm-x/snap-x", project);
            }
            case ProviderCommandKinds.VmKeyAdditionCreate:
            {
                (Vm vm, VmKeyAddition addition) = AVmKeyAddition();
                return queue.EnqueueVmKeyAdditionCreateAsync(vm, addition, request, "vm-x", project, ["ssh-ed25519 AAAA"]);
            }
            case ProviderCommandKinds.VmBackup:
                return queue.EnqueueVmBackupAsync(AVm(), "vm-x", project, new DateOnly(2026, 10, 26));
            case ProviderCommandKinds.VolumeBackup:
                return queue.EnqueueVolumeBackupAsync(AVolume(), "vol-x", project, new DateOnly(2026, 10, 26));
            case ProviderCommandKinds.NetworkCreate:
                return queue.EnqueueNetworkCreateAsync(ANetwork(), request, project, null);
            case ProviderCommandKinds.NetworkDelete:
                return queue.EnqueueNetworkDeleteAsync(ANetwork(), request, "net-x", project);
            case ProviderCommandKinds.FirewallRuleCreate:
            {
                (Network network, FirewallRule rule) = ARule();
                return queue.EnqueueFirewallRuleCreateAsync(network, rule, request, project);
            }
            case ProviderCommandKinds.FirewallRuleDelete:
            {
                (Network network, FirewallRule rule) = ARule();
                return queue.EnqueueFirewallRuleDeleteAsync(network, rule, request, "acl-x/rule", project);
            }
            case ProviderCommandKinds.VmCreate:
                return queue.EnqueueVmCreateAsync(AVm(), request, Medium, "tpl-a", "net-1", project, null);
            case ProviderCommandKinds.VmDelete:
                return queue.EnqueueVmDeleteAsync(AVm(), request, "vm-x", project);
            case ProviderCommandKinds.VmStop:
                return queue.EnqueueVmStopAsync(AVm(), request, "vm-x", project);
            case ProviderCommandKinds.VmStart:
                return queue.EnqueueVmStartAsync(AVm(), request, "vm-x", project);
            case ProviderCommandKinds.LoadBalancerCreate:
                return queue.EnqueueLoadBalancerCreateAsync(ALoadBalancer("mock"), request, project);
            case ProviderCommandKinds.LoadBalancerUpdate:
                return queue.EnqueueLoadBalancerUpdateAsync(ALoadBalancer("mock"), request, project);
            case ProviderCommandKinds.LoadBalancerDelete:
            {
                LoadBalancer lb = ALoadBalancer("mock");
                return queue.EnqueueLoadBalancerDeleteAsync(lb, request, lb.Vip, project);
            }
            case ProviderCommandKinds.TemplateSync:
                return queue.EnqueueTemplateSyncAsync(ATemplate(), "mock", request);
            case ProviderCommandKinds.FunctionRuntimeSync:
                return queue.EnqueueFunctionRuntimeSyncAsync(ARuntime(), "mock", request);
            case ProviderCommandKinds.FunctionDeploy:
            {
                (Function function, FunctionVersion version) = AFunctionVersion("mock");
                return queue.EnqueueFunctionDeployAsync(function, version, request);
            }
            case ProviderCommandKinds.FunctionDelete:
                return queue.EnqueueFunctionDeleteAsync(AFunctionVersion("mock").Function, request);
            case ProviderCommandKinds.ClusterBackup:
                return queue.EnqueueClusterBackupAsync("mock", new DateOnly(2026, 11, 11));
            case ProviderCommandKinds.RestoreDrill:
                return queue.EnqueueRestoreDrillAsync("mock", new DateOnly(2026, 11, 11), "vm", Guid.NewGuid(), "vm-x", "web-1", project, Guid.NewGuid());
            case ProviderCommandKinds.VmRestore:
            {
                (Vm source, Vm copy) = ARestoredVm();
                return queue.EnqueueVmRestoreAsync(source, copy, request, ArchiveKey, project, []);
            }
            case ProviderCommandKinds.VolumeRestore:
            {
                (Volume source, Volume copy) = ARestoredVolume();
                return queue.EnqueueVolumeRestoreAsync(source, copy, request, ArchiveKey, project);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "a kind the queue does not enqueue");
        }
    }

    // What each worker handler reads (its payload.X reads under OpenViCloud/titan-incus-worker:resource/), and nothing
    // else: a field no handler reads is a promise of the contract that no one keeps. A few structs there still declare a
    // field this list drops; it decodes to empty. The cluster is on the row,
    // which the worker reads from the lease (titan-ops architecture/provider-and-worker.md#provider-command-lease).
    public static TheoryData<string, string[]> FieldsTheWorkerReads() => new()
    {
        { ProviderCommandKinds.NetworkCreate, ["cidr", "defaultEgressPolicy", "defaultIngressPolicy", "name", "networkId", "projectName", "publicIpv6RouterAddress", "publicIpv6Subnet", "requestId"] },
        { ProviderCommandKinds.NetworkDelete, ["externalNetworkId", "networkId", "projectName", "requestId"] },
        { ProviderCommandKinds.FirewallRuleCreate, ["cidr", "direction", "endPort", "networkId", "projectName", "protocol", "requestId", "ruleId", "startPort"] },
        { ProviderCommandKinds.FirewallRuleDelete, ["externalRuleId", "networkId", "projectName", "requestId", "ruleId"] },
        { ProviderCommandKinds.VmCreate, ["diskSizeGb", "externalNetworkId", "memoryMb", "name", "projectName", "requestId", "templateId", "userData", "vcpuCount", "vendorData", "vmId"] },
        { ProviderCommandKinds.VmDelete, ["externalVmId", "projectName", "requestId", "vmId"] },
        { ProviderCommandKinds.VmStop, ["externalVmId", "projectName", "requestId", "stopTimeoutSeconds", "vmId"] },
        { ProviderCommandKinds.VmStart, ["externalVmId", "projectName", "requestId", "vmId"] },
        { ProviderCommandKinds.VmSnapshotCreate, ["externalVmId", "projectName", "requestId", "snapshotId", "vmId"] },
        { ProviderCommandKinds.VmSnapshotRevert, ["externalSnapshotId", "projectName", "requestId", "snapshotId", "vmId"] },
        { ProviderCommandKinds.VmSnapshotDelete, ["externalSnapshotId", "projectName", "requestId", "snapshotId", "vmId"] },
        { ProviderCommandKinds.VmKeyAdditionCreate, ["externalVmId", "keyAdditionId", "keyLines", "projectName", "requestId", "vmId"] },
        { ProviderCommandKinds.VolumeCreate, ["name", "projectName", "requestId", "sizeGb", "volumeId"] },
        { ProviderCommandKinds.VolumeAttach, ["externalVmId", "externalVolumeId", "projectName", "requestId", "volumeId"] },
        { ProviderCommandKinds.VolumeDetach, ["externalVolumeId", "projectName", "requestId", "volumeId"] },
        { ProviderCommandKinds.VolumeDelete, ["externalVolumeId", "projectName", "requestId", "volumeId"] },
        { ProviderCommandKinds.VolumeSnapshotCreate, ["externalVolumeId", "projectName", "requestId", "snapshotId", "volumeId"] },
        { ProviderCommandKinds.VolumeSnapshotDelete, ["externalSnapshotId", "projectName", "requestId", "snapshotId", "volumeId"] },
        { ProviderCommandKinds.VolumeSnapshotRestore, ["externalSnapshotId", "name", "newVolumeId", "projectName", "requestId"] },
        { ProviderCommandKinds.VmBackup, ["externalVmId", "projectName", "tenantId", "vmId"] },
        { ProviderCommandKinds.VolumeBackup, ["externalVolumeId", "projectName", "tenantId", "volumeId"] },
        { ProviderCommandKinds.LoadBalancerCreate, ["backends", "externalNetworkId", "listeners", "loadBalancerId", "projectName", "publicIpv6Subnet", "requestId", "vip"] },
        { ProviderCommandKinds.LoadBalancerUpdate, ["backends", "externalNetworkId", "listeners", "loadBalancerId", "projectName", "publicIpv6Subnet", "requestId", "vip"] },
        { ProviderCommandKinds.LoadBalancerDelete, ["externalNetworkId", "loadBalancerId", "projectName", "requestId", "vip"] },
        { ProviderCommandKinds.TemplateSync, ["imageUrl", "name", "osImageName", "requestId", "templateId"] },
        // OpenViCloud/titan-function-worker:resource/runtime/sync.go syncPayload.
        { ProviderCommandKinds.FunctionRuntimeSync, ["image", "name", "requestId", "runtimeId"] },
        // OpenViCloud/titan-function-worker:resource/function/deploy.go and delete.go.
        { ProviderCommandKinds.FunctionDeploy, ["concurrency", "containerLimits", "cpu", "functionId", "handler", "image", "maxInstances", "memoryMiB", "requestId", "tenantId", "tenantQuota", "timeoutSeconds", "versionId"] },
        { ProviderCommandKinds.FunctionDelete, ["functionId", "requestId", "tenantId"] },
        { ProviderCommandKinds.ClusterBackup, [] },
        { ProviderCommandKinds.RestoreDrill, ["externalId", "kind", "name", "projectName", "resourceId", "tenantId"] },
        { ProviderCommandKinds.VmRestore, ["backupKey", "diskSizeGb", "name", "projectName", "requestId", "sourceVmId", "tenantId", "vmId", "volumes"] },
        { ProviderCommandKinds.VolumeRestore, ["backupKey", "name", "newVolumeId", "projectName", "requestId", "sizeGb", "sourceVolumeId", "tenantId"] },
    };

    [Theory]
    [MemberData(nameof(FieldsTheWorkerReads))]
    public async Task EveryKind_CarriesExactlyTheFieldsTheWorkerReads(string kind, string[] fields)
    {
        await EnqueueOne(Queue(), kind, project: "t-0123");

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal(kind, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(fields, payload.RootElement.EnumerateObject().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray());
    }

    // A kind with no row above would carry any payload unchecked.
    [Fact]
    public void FieldsTheWorkerReads_HasARowForEveryKind()
    {
        string[] everyKind = typeof(ProviderCommandKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] listed = FieldsTheWorkerReads().Select(row => (string)row[0]).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(everyKind, listed);
    }

    // A load balancer backend is named by its instance: the worker reads its address from Incus
    // (OpenViCloud/titan-incus-worker:resource/loadbalancer/spec.go).
    [Fact]
    public async Task LoadBalancerBackends_CarryTheInstanceOnly()
    {
        await Queue().EnqueueLoadBalancerCreateAsync(ALoadBalancer("mock"), Guid.NewGuid(), "t-0123");

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        JsonElement backend = Assert.Single(payload.RootElement.GetProperty("backends").EnumerateArray());
        Assert.Equal(["externalVmId"], backend.EnumerateObject().Select(f => f.Name).ToArray());
    }

    // The worker lower-cases both before it maps them (OpenViCloud/titan-incus-worker:resource/firewall/rule.go AclRule);
    // the queue writes the rule the network holds, not the customer's spelling.
    [Fact]
    public async Task FirewallRuleCreate_CarriesTheRuleTheNetworkHolds()
    {
        (Network network, FirewallRule rule) = ARule();

        await Queue().EnqueueFirewallRuleCreateAsync(network, rule, Guid.NewGuid(), "t-0123");

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal("ingress", payload.RootElement.GetProperty("direction").GetString());
        Assert.Equal("tcp", payload.RootElement.GetProperty("protocol").GetString());
        Assert.Equal(22, payload.RootElement.GetProperty("startPort").GetInt32());
        Assert.Equal(22, payload.RootElement.GetProperty("endPort").GetInt32());
        Assert.Equal("10.0.0.0/8", payload.RootElement.GetProperty("cidr").GetString());
        Assert.Equal(network.Id.ToString(), payload.RootElement.GetProperty("networkId").GetString());
    }
}
