using System.Text.Json;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
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

    [Fact]
    public async Task VmCreate_StagesOnePendingRow_KeyedOnTheRequest_WithTheWorkersPayload()
    {
        Guid vmId = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();

        await Queue().EnqueueVmCreateAsync(vmId, requestId, "web-1", "m", 2m, 2, 4096, "tpl-a", "mock", "net-1",
            "t-0123", 20, "#cloud-config\n", vendorData: null);

        ProviderCommand row = Assert.Single(_repo.Rows);
        Assert.Equal($"vm-create-{vmId}-{requestId}", row.Key);
        Assert.Equal(ProviderCommandKinds.VmCreate, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        Assert.Equal(ProviderCommandStatus.Pending, row.Status);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(vmId.ToString(), payload.RootElement.GetProperty("vmId").GetString());
        Assert.Equal(requestId.ToString(), payload.RootElement.GetProperty("requestId").GetString());
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
        await Queue().EnqueueVmCreateAsync(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", 2m, 2, 4096, "tpl-a", "mock", "net-1",
            "t-0123", 20, null, vendorData);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(expected, payload.RootElement.GetProperty("vendorData").GetString());
        Assert.Equal("", payload.RootElement.GetProperty("userData").GetString());
    }

    // One row per request (titan-ops architecture/resource-lifecycle.md#one-key-per-request): a second delete of the same VM is
    // a new request with its own key, never a repeat of the first row.
    [Fact]
    public async Task VmDelete_EachRequest_GetsItsOwnRow_CarryingItsRequestId()
    {
        Guid vmId = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmDeleteAsync(vmId, first, "vm-x", "t-1", "mock");
        await queue.EnqueueVmDeleteAsync(vmId, second, "vm-x", "t-1", "mock");

        Assert.Equal(2, _repo.Rows.Count);
        Assert.Equal($"vm-delete-{vmId}-{first}", _repo.Rows[0].Key);
        Assert.Equal($"vm-delete-{vmId}-{second}", _repo.Rows[1].Key);
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
            return queue.EnqueueVmDeleteAsync(resourceId, requestId, externalId, "t-1", "mock");
        }
        return queue.EnqueueVmBackupAsync(resourceId, externalId, "t-1", requestId, "mock", new DateOnly(2026, 10, 26));
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
            row.Complete(lease, now);
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
        await queue.EnqueueLoadBalancerDeleteAsync(lb.Id, delete, "net-x", lb.Vip, "t-1", "cluster-a");

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
        Guid lbId = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueLoadBalancerDeleteAsync(lbId, first, "net-x", "2001:db8::1:1", "t-1", "mock");
        await queue.EnqueueLoadBalancerDeleteAsync(lbId, second, "net-x", "2001:db8::1:1", "t-1", "mock");

        Assert.Equal([$"load-balancer-delete-{lbId}-{first}", $"load-balancer-delete-{lbId}-{second}"], _repo.Rows.Select(r => r.Key));
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandStatus.Pending, r.Status));
    }

    private static LoadBalancer ALoadBalancer()
    {
        const string subnet = "2001:db8:0:1::/64";
        Guid tenantId = TestTenant.WithRoomToSpare().Id;
        Guid networkId = Guid.NewGuid();
        Vm vm = Vm.Create(tenantId, networkId, "vm-q", "m", "tpl", "cluster-a", 20);
        vm.FinishOperation(vm.Pending!.Id, "vmq", VmState.Running, "10.0.0.5", null);
        return LoadBalancer.Create(tenantId, "web", networkId, "net-x", subnet, "cluster-a", LoadBalancerVip.For(subnet, 1),
            [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [vm]);
    }

    // The VM's keys carry the request id, so stop->start->stop are three rows; no handler enqueues a key twice
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task VmStopAndStart_AreKeyedOnTheirRequest()
    {
        Guid vmId = Guid.NewGuid();
        Guid stop = Guid.NewGuid();
        Guid start = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmStopAsync(vmId, stop, "vm-x", "t-1", "mock");
        await queue.EnqueueVmStartAsync(vmId, start, "vm-x", "t-1", "mock");

        Assert.Equal($"vm-stop-{vmId}-{stop}", _repo.Rows[0].Key);
        Assert.Equal($"vm-start-{vmId}-{start}", _repo.Rows[1].Key);
        Assert.All(_repo.Rows, r => Assert.Equal(ProviderCommandStatus.Pending, r.Status));
    }

    // A volume's and a snapshot's keys carry the request id, as the VM's: each request its own row, and the payload names
    // the request for the worker's report (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task VolumeAndSnapshotRequests_EachGetTheirOwnRow_CarryingTheirRequestId()
    {
        Guid volumeId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        Guid firstAttach = Guid.NewGuid();
        Guid secondAttach = Guid.NewGuid();
        Guid detach = Guid.NewGuid();
        Guid delete = Guid.NewGuid();
        Guid snapshotCreate = Guid.NewGuid();
        Guid snapshotDelete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVolumeAttachAsync(volumeId, firstAttach, "vol-x", "vm-x", "t-1", "mock");
        await queue.EnqueueVolumeAttachAsync(volumeId, secondAttach, "vol-x", "vm-y", "t-1", "mock");
        await queue.EnqueueVolumeDetachAsync(volumeId, detach, "vol-x", "t-1", "mock");
        await queue.EnqueueVolumeDeleteAsync(volumeId, delete, "vol-x", "t-1", "mock");
        await queue.EnqueueVolumeSnapshotCreateAsync(snapshotId, snapshotCreate, volumeId, "vol-x", "t-1", "mock");
        await queue.EnqueueVolumeSnapshotDeleteAsync(snapshotId, snapshotDelete, volumeId, "vol-x/s-x", "t-1", "mock");

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
        Guid vmId = Guid.NewGuid();
        Guid stopRequest = Guid.NewGuid();
        Guid startRequest = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmStopAsync(vmId, stopRequest, "vm-x", "t-1", "mock");
        await queue.EnqueueVmStartAsync(vmId, startRequest, "vm-x", "t-1", "mock");

        using JsonDocument stop = JsonDocument.Parse(_repo.Rows[0].Payload);
        Assert.Equal(stopRequest.ToString(), stop.RootElement.GetProperty("requestId").GetString());
        Assert.False(stop.RootElement.TryGetProperty("intentId", out _));
        using JsonDocument start = JsonDocument.Parse(_repo.Rows[1].Payload);
        Assert.Equal(startRequest.ToString(), start.RootElement.GetProperty("requestId").GetString());
        Assert.False(start.RootElement.TryGetProperty("intentId", out _));
        Assert.Equal($"vm-start-{vmId}-{startRequest}", _repo.Rows[1].Key);
    }

    // A network's and a rule's keys carry the request id, as the volume's: each request its own row, and the payload names
    // the request for the worker's report (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task NetworkAndFirewallRuleRequests_EachGetTheirOwnRow_CarryingTheirRequestId()
    {
        Guid networkId = Guid.NewGuid();
        Guid ruleId = Guid.NewGuid();
        Guid networkCreate = Guid.NewGuid();
        Guid networkDelete = Guid.NewGuid();
        Guid ruleCreate = Guid.NewGuid();
        Guid ruleDelete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueNetworkCreateAsync(networkId, networkCreate, "backend", "mock", "t-1", "10.40.0.0/24", "Deny", "Allow", null, null);
        await queue.EnqueueNetworkDeleteAsync(networkId, networkDelete, "net-x", "t-1", "mock");
        await queue.EnqueueFirewallRuleCreateAsync(ruleId, ruleCreate, networkId, "Ingress", "TCP", 22, 22, "10.0.0.0/8", "net-x", "t-1", "mock");
        await queue.EnqueueFirewallRuleDeleteAsync(ruleId, ruleDelete, networkId, "acl-x/rule", "t-1", "mock");

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
        Guid networkId = Guid.NewGuid();
        Guid ruleId = Guid.NewGuid();
        Guid[] requests = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueNetworkDeleteAsync(networkId, requests[0], "net-x", "t-1", "mock");
        await queue.EnqueueNetworkDeleteAsync(networkId, requests[1], "net-x", "t-1", "mock");
        await queue.EnqueueFirewallRuleDeleteAsync(ruleId, requests[2], networkId, "acl-x/rule", "t-1", "mock");
        await queue.EnqueueFirewallRuleDeleteAsync(ruleId, requests[3], networkId, "acl-x/rule", "t-1", "mock");

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
        Guid snapshotId = Guid.NewGuid();
        Guid vmId = Guid.NewGuid();
        Guid create = Guid.NewGuid();
        Guid revert = Guid.NewGuid();
        Guid delete = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmSnapshotCreateAsync(snapshotId, create, vmId, "vm-x", "nightly", "t-1", "mock");
        await queue.EnqueueVmSnapshotRevertAsync(snapshotId, revert, vmId, "vm-x/s-x", "t-1", "mock");
        await queue.EnqueueVmSnapshotDeleteAsync(snapshotId, delete, vmId, "vm-x/s-x", "t-1", "mock");

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
        Guid snapshotId = Guid.NewGuid();
        Guid vmId = Guid.NewGuid();
        Guid[] requests = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVmSnapshotRevertAsync(snapshotId, requests[0], vmId, "vm-x/s-x", "t-1", "mock");
        await queue.EnqueueVmSnapshotRevertAsync(snapshotId, requests[1], vmId, "vm-x/s-x", "t-1", "mock");
        await queue.EnqueueVmSnapshotDeleteAsync(snapshotId, requests[2], vmId, "vm-x/s-x", "t-1", "mock");
        await queue.EnqueueVmSnapshotDeleteAsync(snapshotId, requests[3], vmId, "vm-x/s-x", "t-1", "mock");

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
        Guid ruleId = Guid.NewGuid();
        Guid networkId = Guid.NewGuid();

        await Queue().EnqueueFirewallRuleDeleteAsync(ruleId, Guid.NewGuid(), networkId, "acl-1", "t-1", "mock");

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(networkId.ToString(), payload.RootElement.GetProperty("networkId").GetString());
    }

    // The worker merges the two policies into the network's default action
    // (titan-ops features/network/design.md#firewall-default-policy): it needs both.
    [Fact]
    public async Task NetworkCreate_CarriesBothPolicies()
    {
        await Queue().EnqueueNetworkCreateAsync(Guid.NewGuid(), Guid.NewGuid(), "backend", "mock", "t-1", "10.40.0.0/24", "Deny", "Allow", null, null);

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
        await Queue().EnqueueNetworkCreateAsync(Guid.NewGuid(), Guid.NewGuid(), "backend", "singapore01", "t-1", null, "Allow", "Allow", subnet, router);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.Equal(wantSubnet, payload.RootElement.GetProperty("publicIpv6Subnet").GetString());
        Assert.Equal(wantRouter, payload.RootElement.GetProperty("publicIpv6RouterAddress").GetString());
    }

    // An old worker would read the removed field; the new payload must not carry it.
    [Fact]
    public async Task VmCreate_NoLongerCarriesEnablePublicIpv6()
    {
        await Queue().EnqueueVmCreateAsync(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", 2m, 2, 4096, "tpl-a", "mock", "net-1",
            "t-0123", 20, null, null);

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(_repo.Rows).Payload);
        Assert.False(payload.RootElement.TryGetProperty("enablePublicIpv6", out _));
    }

    // A rule lives in its network's ACL, inside the tenant's project (titan-ops features/network/design.md#firewall-on-incus).
    [Fact]
    public async Task FirewallRuleCreateAndDelete_CarryTheProject()
    {
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueFirewallRuleCreateAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Ingress", "TCP", 22, 22, "10.0.0.0/8", "net-x", "t-1", "mock");
        await queue.EnqueueFirewallRuleDeleteAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "acl-x/rule", "t-1", "mock");

        foreach (ProviderCommand row in _repo.Rows)
        {
            using JsonDocument payload = JsonDocument.Parse(row.Payload);
            Assert.Equal("t-1", payload.RootElement.GetProperty("projectName").GetString());
            Assert.Equal("mock", payload.RootElement.GetProperty("clusterId").GetString());
        }
        Assert.Equal(2, _repo.Rows.Count);
    }

    // A restore is the new volume's create: keyed like one, so the stuck-request query finds it; keyed on the snapshot, a
    // second restore would be absorbed by the first's open row.
    [Fact]
    public async Task VolumeRestore_IsKeyedAsTheNewVolumesCreate_NotOnTheSnapshot()
    {
        Guid snapshotId = Guid.NewGuid();
        Guid firstVolume = Guid.NewGuid();
        Guid secondVolume = Guid.NewGuid();
        Guid secondRequest = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueVolumeSnapshotRestoreAsync(snapshotId, firstVolume, Guid.NewGuid(), "vol-src/snap-1", "a", "t-1", "mock");
        await queue.EnqueueVolumeSnapshotRestoreAsync(snapshotId, secondVolume, secondRequest, "vol-src/snap-1", "b", "t-1", "mock");

        Assert.Equal(2, _repo.Rows.Count);
        Assert.Equal($"volume-create-{secondVolume}-{secondRequest}", _repo.Rows[1].Key);
        Assert.Equal(ProviderCommandKinds.VolumeSnapshotRestore, _repo.Rows[1].Kind);
    }

    // An Incus image carries no CPU/RAM: sizing travels with the VM (titan-ops features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task TemplateSync_CarriesTheImageAlias_AndNoSizing()
    {
        Guid requestId = Guid.NewGuid();
        await Queue().EnqueueTemplateSyncAsync(Guid.NewGuid(), "mock", requestId, "Alma-9-DB", null, "almalinux/9", null, 40);

        ProviderCommand row = Assert.Single(_repo.Rows);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("almalinux/9", payload.RootElement.GetProperty("osImageName").GetString());
        Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetGuid());
        Assert.False(payload.RootElement.TryGetProperty("cpuUnits", out _));
        Assert.False(payload.RootElement.TryGetProperty("memoryMb", out _));
        Assert.False(payload.RootElement.TryGetProperty("vcpuCount", out _));
    }

    // The worker imports the distro's own image when the template names one; none ⇒ "" and it pulls osImageName
    // (titan-ops features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task TemplateSync_CarriesTheImageUrl_OrAnEmptyOne()
    {
        const string url = "https://cloud-images.ubuntu.com/releases/jammy/release/ubuntu-22.04-server-cloudimg-{arch}.img";
        await Queue().EnqueueTemplateSyncAsync(Guid.NewGuid(), "mock", Guid.NewGuid(), "Ubuntu-22.04-Standard", null, "ubuntu/22.04", url, 25);
        await Queue().EnqueueTemplateSyncAsync(Guid.NewGuid(), "mock", Guid.NewGuid(), "Alma-9-DB", null, "almalinux/9", null, 40);

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
        Guid templateId = Guid.NewGuid();
        Guid inSingapore = Guid.NewGuid(), inMock = Guid.NewGuid(), inMockAgain = Guid.NewGuid();
        ProviderCommandQueue queue = Queue();

        await queue.EnqueueTemplateSyncAsync(templateId, "singapore01", inSingapore, "Alma-9-DB", null, "almalinux/9", null, 40);
        await queue.EnqueueTemplateSyncAsync(templateId, "mock", inMock, "Alma-9-DB", null, "almalinux/9", null, 40);
        await queue.EnqueueTemplateSyncAsync(templateId, "mock", inMockAgain, "Alma-9-DB", null, "almalinux/9", null, 40);

        Assert.Equal(
            new[]
            {
                $"template-sync-{templateId}-singapore01-{inSingapore}", $"template-sync-{templateId}-mock-{inMock}",
                $"template-sync-{templateId}-mock-{inMockAgain}",
            },
            _repo.Rows.Select(r => r.Key).ToArray());
    }

    // One backup a day per resource (titan-ops features/backup/design.md#incus-backup):
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
    // which project cannot be carried out (titan-ops architecture/provider-and-worker.md#translate-titan-ids).
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
        await Queue().EnqueueVolumeCreateAsync(Guid.NewGuid(), Guid.NewGuid(), "data-1", "mock", 50, "t-0123");

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
                return queue.EnqueueVolumeCreateAsync(id, other, "data-1", cluster, 10, project);
            case ProviderCommandKinds.VolumeAttach:
                return queue.EnqueueVolumeAttachAsync(id, other, "vol-x", "vm-x", project, cluster);
            case ProviderCommandKinds.VolumeDetach:
                return queue.EnqueueVolumeDetachAsync(id, other, "vol-x", project, cluster);
            case ProviderCommandKinds.VolumeDelete:
                return queue.EnqueueVolumeDeleteAsync(id, other, "vol-x", project, cluster);
            case ProviderCommandKinds.VolumeSnapshotCreate:
                return queue.EnqueueVolumeSnapshotCreateAsync(id, other, Guid.NewGuid(), "vol-x", project, cluster);
            case ProviderCommandKinds.VolumeSnapshotDelete:
                return queue.EnqueueVolumeSnapshotDeleteAsync(id, other, Guid.NewGuid(), "vol-x/snap-x", project, cluster);
            case ProviderCommandKinds.VolumeSnapshotRestore:
                return queue.EnqueueVolumeSnapshotRestoreAsync(id, other, Guid.NewGuid(), "vol-x/snap-x", "restored", project, cluster);
            case ProviderCommandKinds.VmSnapshotCreate:
                return queue.EnqueueVmSnapshotCreateAsync(id, Guid.NewGuid(), other, "vm-x", "before-upgrade", project, cluster);
            case ProviderCommandKinds.VmSnapshotRevert:
                return queue.EnqueueVmSnapshotRevertAsync(id, Guid.NewGuid(), other, "vm-x/snap-x", project, cluster);
            case ProviderCommandKinds.VmSnapshotDelete:
                return queue.EnqueueVmSnapshotDeleteAsync(id, Guid.NewGuid(), other, "vm-x/snap-x", project, cluster);
            case ProviderCommandKinds.VmBackup:
                return queue.EnqueueVmBackupAsync(id, "vm-x", project, other, cluster, new DateOnly(2026, 10, 26));
            case ProviderCommandKinds.VolumeBackup:
                return queue.EnqueueVolumeBackupAsync(id, "vol-x", project, other, cluster, new DateOnly(2026, 10, 26));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a storage or snapshot kind");
        }
    }
}
