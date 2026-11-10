using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Queries.Compute.LoadBalancer;
using Titan.Application.Queries.Compute.LoadBalancer.Contracts;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// titan-ops features/load-balancer/design.md, acceptance tests A-E of the NETWORK-LOAD-BALANCER iteration. The handlers
// over a fake repository; the provider command queue is a Moq mock where the command itself is the assertion.
public class LoadBalancerServiceTests
{
    private readonly Tenant _tenant = TestTenant.WithRoomToSpare();
    private Guid TenantId => _tenant.Id;
    private string Project => "t-" + TenantId.ToString("N");

    private readonly FakeLoadBalancerRepository _repo = new();
    private readonly Mock<INetworkRepository> _networks = new();
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly List<Vm> _knownVms = new();

    public LoadBalancerServiceTests()
    {
        // The handlers lock the rows they read (titan-ops architecture/domain-services.md#cross-aggregate-lock); a mock has no
        // rows, so the locked read answers what the plain one does.
        _networks.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => _networks.Object.FindByIdAsync(id, ct));
        _vms.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _knownVms.FirstOrDefault(v => v.Id == id));
        _currentUser.Setup(u => u.TenantId).Returns(() => TenantId);
        _tenants.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == TenantId ? _tenant : null);
        _vms.Setup(r => r.ListByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => _knownVms.Where(v => ids.Contains(v.Id)).ToList());
    }

    private Network NetworkWithIpv6(bool usable = true, Guid? tenantId = null)
    {
        Tenant owner = tenantId == null ? _tenant : TestTenant.Active("other", "kc-o");
        var network = TestNetworks.CreatingWithPublicIpv6(owner, $"net-{Guid.NewGuid():N}"[..12], "cluster-1");
        if (usable)
        {
            network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        }
        _networks.Setup(r => r.FindByIdAsync(network.Id, It.IsAny<CancellationToken>())).ReturnsAsync(network);
        return network;
    }

    private Network NetworkWithoutIpv6()
    {
        var network = Network.Create(_tenant.Id, NetworkName.Parse("plain"), "cluster-1", null, wantsPublicIpv6: false);
        network.MarkActive("net-plain", "10.0.0.0/24", "10.0.0.1");
        _networks.Setup(r => r.FindByIdAsync(network.Id, It.IsAny<CancellationToken>())).ReturnsAsync(network);
        return network;
    }

    private Vm VmOn(Network network, Titan.Domain.Aggregates.Tenant.Tenant? owner = null)
    {
        var vm = Vm.Create((owner ?? _tenant).Id, network.Id, $"vm-{_knownVms.Count}", "m", "tpl", "cluster-1", 20);
        vm.MarkRunning($"vm{_knownVms.Count:D26}", "10.0.0.5");
        _knownVms.Add(vm);
        return vm;
    }

    private static readonly LoadBalancerListenerInput Http = new("TCP", 80, 8080);
    private static readonly LoadBalancerListenerInput Dns = new("UDP", 53, 53);

    private static List<LoadBalancerBackendInput> Backends(params Vm[] vms) => vms.Select(v => new LoadBalancerBackendInput(v.Id)).ToList();

    private CreateLoadBalancerCommandHandler CreateHandler() =>
        new(_repo, _networks.Object, _vms.Object, _tenants.Object, _currentUser.Object, _commands.Object, _activity);
    private UpdateLoadBalancerCommandHandler UpdateHandler() =>
        new(_repo, _vms.Object, _currentUser.Object, _commands.Object, _activity);
    private DeleteLoadBalancerCommandHandler DeleteHandler() =>
        new(_repo, _currentUser.Object, _commands.Object, _activity);
    private EndLoadBalancerRequestCommandHandler EndHandler() => new(_repo, _activity);
    private ObserveLoadBalancerCommandHandler ObserveHandler() => new(_repo);

    private Task End(LoadBalancer lb, Guid requestId, string outcome, string? externalId = null, string? reason = null,
        string? detail = null) =>
        EndHandler().HandleAsync(new EndLoadBalancerRequestCommand(lb.Id, requestId, outcome, externalId, reason, detail));
    private RenameLoadBalancerCommandHandler RenameHandler() => new(_repo, _currentUser.Object);

    private async Task<LoadBalancer> CreatedAsync(Network network, params Vm[] vms)
    {
        var result = await CreateHandler().HandleAsync(
            new CreateLoadBalancerCommand($"lb-{_repo.Rows.Count}", network.Id, [Http], Backends(vms)));
        return _repo.Rows.Single(lb => lb.Id == result.LoadBalancerId);
    }

    private async Task<LoadBalancer> OnIncusAsync(Network network, params Vm[] vms)
    {
        LoadBalancer lb = await CreatedAsync(network, vms);
        lb.MarkCreated(lb.Vip);
        _commands.Invocations.Clear();
        _activity.Events.Clear();
        return lb;
    }

    private static async Task<string> CodeOf(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (BusinessException e) { return e.Code; }
        catch (NotFoundException e) { return e.Code; }
        catch (ConflictException e) { return e.Code; }
        catch (BadRequestException e) { return e.Code; }
        return "none";
    }

    // ---- create (A1-A8, B2)

    [Fact]
    public async Task Create_OnANetworkWithPublicIpv6_StagesTheLoadBalancer_ItsCommand_AndTheRequest()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        Vm b = VmOn(network);

        var result = await CreateHandler().HandleAsync(new CreateLoadBalancerCommand("web", network.Id, [Http], Backends(a, b)));

        LoadBalancer lb = _repo.Rows.Single();
        Assert.Equal(result.LoadBalancerId, lb.Id);
        Assert.Equal("2001:41d0:d00:d405::1:1", lb.Vip);
        Assert.Equal("net-ext", lb.ExternalNetworkId);
        Assert.Equal("2001:41d0:d00:d405::/64", lb.NetworkSubnet);
        Assert.Equal("cluster-1", lb.ClusterId);
        Assert.Equal(LoadBalancerStatus.Creating, lb.Status);
        Assert.Equal(1, _repo.Saves);
        _commands.Verify(c => c.EnqueueLoadBalancerCreateAsync(lb, lb.Operation.PendingId!.Value, Project, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { "load_balancer.create_requested" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Create_OnANetworkWithoutPublicIpv6_IsRefused_AndNothingIsStaged()
    {
        Network network = NetworkWithoutIpv6();
        Vm vm = VmOn(network);

        string code = await CodeOf(() => CreateHandler().HandleAsync(new CreateLoadBalancerCommand("web", network.Id, [Http], Backends(vm))));

        Assert.Equal("LOAD_BALANCER_NEEDS_PUBLIC_IPV6", code);
        Assert.Empty(_repo.Rows);
        _commands.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_OnANetworkIncusHasNotCreated_IsRefused()
    {
        Network network = NetworkWithIpv6(usable: false);

        string code = await CodeOf(() => CreateHandler().HandleAsync(
            new CreateLoadBalancerCommand("web", network.Id, [Http], [new(Guid.NewGuid())])));

        Assert.Equal("NETWORK_INVALID_STATE", code);
        Assert.Empty(_repo.Rows);
    }

    // A missing VM and another tenant's VM answer alike (titan-ops architecture/http-contract.md#tenant-scoping).
    [Fact]
    public async Task Create_WithAnotherTenantsVm_OrAMissingOne_IsRefusedAlike()
    {
        Network network = NetworkWithIpv6();
        Vm foreign = VmOn(network, owner: TestTenant.WithRoomToSpare());

        string foreignCode = await CodeOf(() => CreateHandler().HandleAsync(new CreateLoadBalancerCommand("web", network.Id, [Http], Backends(foreign))));
        string missingCode = await CodeOf(() => CreateHandler().HandleAsync(new CreateLoadBalancerCommand("web", network.Id, [Http], [new(Guid.NewGuid())])));

        Assert.Equal("LOAD_BALANCER_BACKEND_INVALID", foreignCode);
        Assert.Equal("LOAD_BALANCER_BACKEND_INVALID", missingCode);
        Assert.Empty(_repo.Rows);
    }

    [Fact]
    public async Task Create_AtTheTenantsCap_IsRefused()
    {
        _tenant.UpdateQuota(TenantQuota.Create(9, 9, 9, 9, 9, 9, 9, 9, 1));
        Network network = NetworkWithIpv6();
        Vm vm = VmOn(network);
        await CreatedAsync(network, vm);

        string code = await CodeOf(() => CreateHandler().HandleAsync(new CreateLoadBalancerCommand("api", network.Id, [Http], Backends(vm))));

        Assert.Equal("LOAD_BALANCER_QUOTA_EXCEEDED", code);
        Assert.Single(_repo.Rows);
    }

    [Fact]
    public async Task Create_WithATakenName_IsConflict_ButTheNameOfADeletedOneIsFree()
    {
        Network network = NetworkWithIpv6();
        Vm vm = VmOn(network);
        LoadBalancer first = await OnIncusAsync(network, vm);

        string code = await CodeOf(() => CreateHandler().HandleAsync(new CreateLoadBalancerCommand(first.Name, network.Id, [Http], Backends(vm))));
        first.Delete();
        first.MarkDeleted();
        var again = await CreateHandler().HandleAsync(new CreateLoadBalancerCommand(first.Name, network.Id, [Http], Backends(vm)));

        Assert.Equal("LOAD_BALANCER_NAME_TAKEN", code);
        Assert.NotEqual(first.Id, again.LoadBalancerId);
    }

    [Fact]
    public async Task Create_OnAnotherTenantsNetwork_IsNotFound()
    {
        Network foreign = NetworkWithIpv6(tenantId: Guid.NewGuid());

        string code = await CodeOf(() => CreateHandler().HandleAsync(
            new CreateLoadBalancerCommand("web", foreign.Id, [Http], [new(Guid.NewGuid())])));

        Assert.Equal("NETWORK_NOT_FOUND", code);
    }

    [Fact]
    public async Task Create_TheSecondOnANetwork_TakesTheNextVip_AndADeletedOnesNumberComesBack()
    {
        Network network = NetworkWithIpv6();
        Vm vm = VmOn(network);
        LoadBalancer first = await OnIncusAsync(network, vm);

        var second = await CreateHandler().HandleAsync(new CreateLoadBalancerCommand("api", network.Id, [Http], Backends(vm)));
        first.Delete();
        first.MarkDeleted();
        var third = await CreateHandler().HandleAsync(new CreateLoadBalancerCommand("dns", network.Id, [Dns], Backends(vm)));

        Assert.Equal(2, _repo.Rows.Single(lb => lb.Id == second.LoadBalancerId).VipIndex);
        Assert.Equal(1, _repo.Rows.Single(lb => lb.Id == third.LoadBalancerId).VipIndex);
    }

    // ---- update (C1-C4)

    [Fact]
    public async Task Update_StagesACommandKeyedOnTheNewRequest()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        Vm b = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);

        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http, Dns], Backends(a, b)));

        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
        _commands.Verify(c => c.EnqueueLoadBalancerUpdateAsync(lb, lb.Operation.PendingId!.Value, Project, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { "load_balancer.update_requested" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Update_ToWhatIncusAlreadyHas_SendsNothing()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        int saves = _repo.Saves;

        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http], Backends(a)));

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        _commands.VerifyNoOtherCalls();
        Assert.Empty(_activity.Events);
        Assert.Equal(saves, _repo.Saves);
    }

    // The same PUT twice (a double click): the second is the first's repeat, not a conflict.
    [Fact]
    public async Task Update_Repeated_WhileItWaits_SendsNothingMore()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http, Dns], Backends(a)));
        Guid request = lb.Operation.PendingId!.Value;
        _commands.Invocations.Clear();
        _activity.Events.Clear();

        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns, Http], Backends(a)));

        Assert.Equal(request, lb.Operation.PendingId!.Value);
        Assert.Empty(_activity.Events);
        _commands.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_ToSomethingElse_WhileAnotherWaits_IsInvalidState()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer creating = await CreatedAsync(network, a);

        string code = await CodeOf(() => UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(creating.Id, [Dns], Backends(a))));

        Assert.Equal("LOAD_BALANCER_INVALID_STATE", code);
    }

    [Fact]
    public async Task Update_OfAnotherTenantsLoadBalancer_IsNotFound()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        _currentUser.Setup(u => u.TenantId).Returns(Guid.NewGuid());

        string code = await CodeOf(() => UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], [])));

        Assert.Equal("LOAD_BALANCER_NOT_FOUND", code);
    }

    // ---- delete (E1-E3, A10)

    [Fact]
    public async Task Delete_StagesTheDeleteCommand_WithTheNetworkAndVip()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));

        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        _commands.Verify(c => c.EnqueueLoadBalancerDeleteAsync(It.Is<global::Titan.Domain.Aggregates.LoadBalancer.LoadBalancer>(a22 => a22.Id == lb.Id && a22.ExternalNetworkId == "net-ext" && a22.ClusterId == "cluster-1"), lb.Operation.PendingId!.Value, lb.Vip, Project, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { "load_balancer.delete_requested" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Delete_Repeated_OrOfADeletedOne_SendsNothing()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer deleting = await OnIncusAsync(network, VmOn(network));
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(deleting.Id));
        _commands.Invocations.Clear();
        _activity.Events.Clear();

        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(deleting.Id));
        deleting.MarkDeleted();
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(deleting.Id));

        _commands.VerifyNoOtherCalls();
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Delete_OfACreateThatGaveUpBeforeIncus_IsDeletedHere_WithBothActivities()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await CreatedAsync(network, VmOn(network));
        lb.MarkRequestFailed("Load balancer creation failed.", null);
        _commands.Invocations.Clear();
        _activity.Events.Clear();

        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        Assert.True(lb.IsDeleted);
        _commands.VerifyNoOtherCalls();
        Assert.Equal(new[] { "load_balancer.delete_requested", "load_balancer.deleted" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Delete_WhileTheCreateWaits_IsInvalidState()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await CreatedAsync(network, VmOn(network));

        string code = await CodeOf(() => DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id)));

        Assert.Equal("LOAD_BALANCER_INVALID_STATE", code);
    }

    // ---- worker reports (A2, C2, C5, C6, E1, E7)

    [Fact]
    public async Task Done_OfTheCreate_RecordsTheOutcomeOnce()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await CreatedAsync(network, VmOn(network));
        Guid create = lb.Operation.PendingId!.Value;
        _activity.Events.Clear();

        await End(lb, create, "Done", lb.Vip);
        await End(lb, create, "Done", lb.Vip);

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(new[] { "load_balancer.created" }, _activity.Events.Select(e => e.Action));
        Assert.Equal(TenantId, _activity.Events.Single().OutcomeTenantId);
    }

    [Fact]
    public async Task Done_OfTheWaitingUpdate_RecordsTheOutcome_AnotherRequestChangesNothing()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid update = lb.Operation.PendingId!.Value;
        _activity.Events.Clear();

        await End(lb, Guid.NewGuid(), "Done");
        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
        await End(lb, update, "Done");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(new[] { "load_balancer.updated" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task UpdateGivesUp_PutsTheConfigurationBack_AndKeepsTheReason()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid update = lb.Operation.PendingId!.Value;

        await End(lb, update, "Failed", reason: "Load balancer update failed.", detail: "raw");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(new[] { 80 }, lb.DesiredListeners.Select(l => l.Port));
        Assert.Equal("Load balancer update failed.", lb.Operation.FailureReason);
        Assert.Equal(new[] { "load_balancer.update_requested", "load_balancer.update_failed" }, _activity.Events.Select(e => e.Action));
    }

    // A delete replaced the update: the update's late give-up must not abandon the delete.
    [Fact]
    public async Task AGiveUpOfAReplacedUpdate_LeavesTheWaitingDelete()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid update = lb.Operation.PendingId!.Value;
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        await End(lb, update, "Failed", reason: "Load balancer update failed.");

        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        Assert.Null(lb.Operation.FailureReason);
    }

    [Fact]
    public async Task DeleteGivesUp_PutsTheLoadBalancerBack()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        await End(lb, lb.Operation.PendingId!.Value, "Failed", reason: "Load balancer deletion failed.");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.True(lb.OccupiesQuota);
        Assert.Contains("load_balancer.delete_failed", _activity.Events.Select(e => e.Action));
    }

    // The give-up found the VIP on Incus: the create ends Failed with the VIP kept, so the delete goes through Incus;
    // a failure is not the create's outcome (titan-ops architecture/resource-lifecycle.md#kinds).
    [Fact]
    public async Task ACreatesGiveUpCarryingTheVip_EndsTheCreateFailed_KeepingTheVip_AndRecordsNoOutcome()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await CreatedAsync(network, VmOn(network));
        _activity.Events.Clear();

        await End(lb, lb.Operation.PendingId!.Value, "Failed", lb.Vip, reason: "x");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal("x", lb.Operation.FailureReason);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Observed_Absent_IsNotFound_AndTheDeletesDone_RecordsTheOutcome()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
        _activity.Events.Clear();

        await ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(lb.Id, false));
        Assert.Null(lb.InstanceStatus);
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        await End(lb, lb.Operation.PendingId!.Value, "Done");

        Assert.True(lb.IsDeleted);
        Assert.Equal(new[] { "load_balancer.deleted" }, _activity.Events.Select(e => e.Action));
    }

    // ---- queries

    [Fact]
    public async Task List_ShowsTheAskedConfiguration_AndHidesDeleted()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer kept = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(kept.Id, [Http, Dns], Backends(a)));
        LoadBalancer gone = await OnIncusAsync(network, a);
        gone.Delete();
        gone.MarkDeleted();

        List<LoadBalancerQueryResult> rows = await new ListLoadBalancersQueryHandler(_repo, _currentUser.Object)
            .HandleAsync(new ListLoadBalancersQuery());

        LoadBalancerQueryResult row = Assert.Single(rows);
        Assert.Equal(kept.Id, row.Id);
        Assert.Equal(new[] { "TCP 80->8080", "UDP 53->53" }, row.Listeners.Select(l => $"{l.Protocol} {l.Port}->{l.TargetPort}"));
        Assert.Equal(new[] { a.Id }, row.Backends.Select(b => b.VmId));
        Assert.Equal(LoadBalancerStatus.Updating, row.Status);
        Assert.Equal("2001:41d0:d00:d405::1:1", row.Vip);
    }

    [Fact]
    public async Task Reconcilable_CarriesTheProjectNetworkAndVip_OfThoseOnIncus()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer onIncus = await OnIncusAsync(network, a);
        await CreatedAsync(network, a);

        var rows = await new ListReconcilableLoadBalancersQueryHandler(_repo).HandleAsync(new ListReconcilableLoadBalancersQuery("cluster-1"));

        ReconcilableLoadBalancerQueryResult row = Assert.Single(rows);
        Assert.Equal(new ReconcilableLoadBalancerQueryResult(onIncus.Id, Project, "net-ext", onIncus.Vip, "Exists"), row);
    }

    // What the worker compares with is its own word, read off the status and what Incus was last seen holding
    // (titan-ops features/load-balancer/design.md#lb-lifecycle): a delete in flight on a load balancer Incus lost reads
    // NotFound, so the worker does not report it again every cycle.
    [Fact]
    public async Task Reconcilable_CurrentExistence_IsExists_OnlyWhileIncusIsKnownToHoldTheVip()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer available = await OnIncusAsync(network, a);
        LoadBalancer notFound = await OnIncusAsync(network, a);
        notFound.MarkNotFound();
        LoadBalancer updatingSeen = await OnIncusAsync(network, a);
        updatingSeen.Update([new LoadBalancerListenerSpec(LoadBalancerProtocol.UDP, 53, 53)], [a]);
        LoadBalancer updatingLost = await OnIncusAsync(network, a);
        updatingLost.Update([new LoadBalancerListenerSpec(LoadBalancerProtocol.UDP, 53, 53)], [a]);
        updatingLost.MarkNotFound();
        LoadBalancer deletingSeen = await OnIncusAsync(network, a);
        deletingSeen.Delete();
        LoadBalancer deletingLost = await OnIncusAsync(network, a);
        deletingLost.MarkNotFound();
        deletingLost.Delete();

        var rows = await new ListReconcilableLoadBalancersQueryHandler(_repo).HandleAsync(new ListReconcilableLoadBalancersQuery("cluster-1"));

        Assert.Equal(
            new Dictionary<Guid, string>
            {
                [available.Id] = "Exists", [notFound.Id] = "NotFound", [updatingSeen.Id] = "Exists",
                [updatingLost.Id] = "NotFound", [deletingSeen.Id] = "Exists", [deletingLost.Id] = "NotFound",
            },
            rows.ToDictionary(r => r.Id, r => r.CurrentExistence));
    }

    // The one status is the only state word on /v1 (titan-ops features/load-balancer/design.md#lb-lifecycle); the failure
    // detail is the operator's, never the tenant's.
    [Fact]
    public async Task TenantLoadBalancerQueryResult_HasOneStatus_AndNoExistenceOrTransition()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));

        using var json = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(LoadBalancerQueryResult.From(lb),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        string[] names = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal("Available", json.RootElement.GetProperty("status").GetString());
        Assert.Contains("requestFailureReason", names);
        Assert.Contains("requestFailedAt", names);
        Assert.DoesNotContain("requestFailureDetail", names);
        Assert.DoesNotContain("currentExistence", names);
        Assert.DoesNotContain("desiredExistence", names);
        Assert.DoesNotContain("transition", names);
    }

    // A repeat of the delete is answered like the first; a PUT of the configuration a load balancer being deleted already
    // has is not a repeat: the aggregate refuses it (titan-ops features/load-balancer/design.md#lb-lifecycle).
    [Fact]
    public async Task Update_ToTheSameConfiguration_WhileDeleting_IsInvalidState_NotARepeat()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
        _commands.Invocations.Clear();

        string code = await CodeOf(() => UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http], Backends(a))));

        Assert.Equal("LOAD_BALANCER_INVALID_STATE", code);
        _commands.VerifyNoOtherCalls();
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
    }

    [Fact]
    public async Task Delete_OfALoadBalancerIncusLost_GoesThroughIncus()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        await ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(lb.Id, false));
        Assert.Equal(LoadBalancerStatus.NotFound, lb.Status);

        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        _commands.Verify(c => c.EnqueueLoadBalancerDeleteAsync(It.Is<global::Titan.Domain.Aggregates.LoadBalancer.LoadBalancer>(a23 => a23.Id == lb.Id && a23.ExternalNetworkId == "net-ext" && a23.ClusterId == "cluster-1"), lb.Operation.PendingId!.Value, lb.Vip, Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_WhileTheUpdateWaits_SendsItsOwnCommand_AndTheUpdatesLateDoneChangesNothing()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid update = lb.Operation.PendingId!.Value;
        _activity.Events.Clear();

        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
        Guid delete = lb.Operation.PendingId!.Value;
        await End(lb, update, "Done");

        Assert.NotEqual(update, delete);
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        _commands.Verify(c => c.EnqueueLoadBalancerDeleteAsync(It.Is<global::Titan.Domain.Aggregates.LoadBalancer.LoadBalancer>(a24 => a24.Id == lb.Id && a24.ExternalNetworkId == "net-ext" && a24.ClusterId == "cluster-1"), delete, lb.Vip, Project, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { "load_balancer.delete_requested" }, _activity.Events.Select(e => e.Action));
    }

    // ---- rename (titan-ops features/load-balancer/design.md#lb-rename)

    [Fact]
    public async Task Rename_ChangesTheName_AndSendsNoCommand()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));

        int saves = _repo.Saves;

        await RenameHandler().HandleAsync(new RenameLoadBalancerCommand(lb.Id, "api"));

        Assert.Equal("api", lb.Name);
        Assert.Empty(_commands.Invocations);
        Assert.Equal(saves + 1, _repo.Saves);
    }

    [Fact]
    public async Task Rename_ToItsOwnName_IsANoOp()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        int saves = _repo.Saves;

        await RenameHandler().HandleAsync(new RenameLoadBalancerCommand(lb.Id, lb.Name));

        Assert.Equal(saves, _repo.Saves);
    }

    [Fact]
    public async Task Rename_ToANameTakenInTheTenant_IsNameTaken()
    {
        Network network = NetworkWithIpv6();
        Vm vm = VmOn(network);
        LoadBalancer first = await OnIncusAsync(network, vm);
        LoadBalancer second = await OnIncusAsync(network, vm);

        string code = await CodeOf(() => RenameHandler().HandleAsync(new RenameLoadBalancerCommand(second.Id, first.Name)));

        Assert.Equal("LOAD_BALANCER_NAME_TAKEN", code);
        Assert.NotEqual(first.Name, second.Name);
    }

    [Fact]
    public async Task Rename_OfAnotherTenantsLoadBalancer_IsNotFound()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        _currentUser.Setup(u => u.TenantId).Returns(Guid.NewGuid());

        string code = await CodeOf(() => RenameHandler().HandleAsync(new RenameLoadBalancerCommand(lb.Id, "api")));

        Assert.Equal("LOAD_BALANCER_NOT_FOUND", code);
    }
}
