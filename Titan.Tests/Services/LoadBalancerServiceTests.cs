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

namespace Titan.Tests.Services;

// titan-ops features/load-balancer/design.md, acceptance tests A-E of the NETWORK-LOAD-BALANCER iteration. The handlers
// over a fake repository; the provider command queue is a Moq mock where the command itself is the assertion.
public class LoadBalancerServiceTests
{
    private const string Block = "2001:41d0:d00:d400::/56";
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
        _currentUser.Setup(u => u.TenantId).Returns(() => TenantId);
        _tenants.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == TenantId ? _tenant : null);
        _vms.Setup(r => r.ListByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => _knownVms.Where(v => ids.Contains(v.Id)).ToList());
    }

    private Network NetworkWithIpv6(bool usable = true, Guid? tenantId = null)
    {
        Tenant owner = tenantId == null ? _tenant : TestTenant.Active("other", "kc-o", "o@o.com");
        var network = Network.Create(owner, 0, 0, $"net-{Guid.NewGuid():N}"[..12], "cluster-1", NetworkPublicIpv6.For(Block, 5));
        if (usable)
        {
            network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        }
        _networks.Setup(r => r.FindByIdAsync(network.Id, It.IsAny<CancellationToken>())).ReturnsAsync(network);
        return network;
    }

    private Network NetworkWithoutIpv6()
    {
        var network = Network.Create(_tenant, 0, 0, "plain", "cluster-1", null);
        network.MarkActive("net-plain", "10.0.0.0/24", "10.0.0.1");
        _networks.Setup(r => r.FindByIdAsync(network.Id, It.IsAny<CancellationToken>())).ReturnsAsync(network);
        return network;
    }

    private Vm VmOn(Network network, Guid? tenantId = null)
    {
        var vm = Vm.CreateUnchecked(tenantId ?? TenantId, network.Id, $"vm-{_knownVms.Count}", "m", "tpl", "cluster-1", 20);
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
    private UpdateLoadBalancerStatusCommandHandler StatusHandler() => new(_repo, _activity);
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
        Assert.Equal(LoadBalancerTransition.Creating, lb.Transition);
        Assert.Equal(1, _repo.Saves);
        _commands.Verify(c => c.EnqueueLoadBalancerCreateAsync(lb, Project, It.IsAny<CancellationToken>()), Times.Once);
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
        Vm foreign = VmOn(network, tenantId: Guid.NewGuid());

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
        first.RequestDelete();
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
        first.RequestDelete();
        first.MarkDeleted();
        var third = await CreateHandler().HandleAsync(new CreateLoadBalancerCommand("dns", network.Id, [Dns], Backends(vm)));

        Assert.Equal(2, _repo.Rows.Single(lb => lb.Id == second.LoadBalancerId).VipIndex);
        Assert.Equal(1, _repo.Rows.Single(lb => lb.Id == third.LoadBalancerId).VipIndex);
    }

    // ---- update (C1-C4)

    [Fact]
    public async Task Update_StagesACommandKeyedOnTheNewIntent()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        Vm b = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);

        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http, Dns], Backends(a, b)));

        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
        _commands.Verify(c => c.EnqueueLoadBalancerUpdateAsync(lb, lb.PendingIntentId!.Value, Project, It.IsAny<CancellationToken>()), Times.Once);
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

        Assert.Null(lb.Transition);
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
        Guid intent = lb.PendingIntentId!.Value;
        _commands.Invocations.Clear();

        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns, Http], Backends(a)));

        Assert.Equal(intent, lb.PendingIntentId);
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

        Assert.Equal(LoadBalancerTransition.Deleting, lb.Transition);
        _commands.Verify(c => c.EnqueueLoadBalancerDeleteAsync(lb.Id, "net-ext", lb.Vip, Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
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

        Assert.True(lb.IsTerminal);
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
    public async Task Created_RecordsTheOutcomeOnce()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await CreatedAsync(network, VmOn(network));
        _activity.Events.Clear();

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Created", ExternalId: lb.Vip));
        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Created", ExternalId: lb.Vip));

        Assert.Equal(CurrentExistence.Exists, lb.CurrentExistence);
        Assert.Equal(new[] { "load_balancer.created" }, _activity.Events.Select(e => e.Action));
        Assert.Equal(TenantId, _activity.Events.Single().OutcomeTenantId);
    }

    [Fact]
    public async Task Updated_ForTheWaitingIntent_RecordsTheOutcome_AnotherIntentChangesNothing()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid intent = lb.PendingIntentId!.Value;
        _activity.Events.Clear();

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Updated", IntentId: Guid.NewGuid()));
        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Updated", IntentId: intent));

        Assert.Null(lb.Transition);
        Assert.Equal(new[] { "load_balancer.updated" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task UpdateGivesUp_PutsTheConfigurationBack_AndKeepsTheReason()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid intent = lb.PendingIntentId!.Value;

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "RequestFailed", IntentId: intent,
            Reason: "Load balancer update failed.", Detail: "raw"));

        Assert.Null(lb.Transition);
        Assert.Equal(new[] { 80 }, lb.DesiredListeners.Select(l => l.Port));
        Assert.Equal("Load balancer update failed.", lb.RequestFailureReason);
    }

    // A delete replaced the update: the update's late give-up must not abandon the delete.
    [Fact]
    public async Task AGiveUpOfAReplacedUpdate_LeavesTheWaitingDelete()
    {
        Network network = NetworkWithIpv6();
        Vm a = VmOn(network);
        LoadBalancer lb = await OnIncusAsync(network, a);
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], Backends(a)));
        Guid update = lb.PendingIntentId!.Value;
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "RequestFailed", IntentId: update,
            Reason: "Load balancer update failed."));

        Assert.Equal(LoadBalancerTransition.Deleting, lb.Transition);
        Assert.Null(lb.RequestFailureReason);
    }

    [Fact]
    public async Task DeleteGivesUp_PutsTheLoadBalancerBack()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "RequestFailed", Reason: "Load balancer deletion failed."));

        Assert.Null(lb.Transition);
        Assert.Equal(DesiredExistence.Exists, lb.DesiredExistence);
    }

    [Fact]
    public async Task ACreatesGiveUpCarryingTheVip_IsTheCreateDone()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await CreatedAsync(network, VmOn(network));
        _activity.Events.Clear();

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "RequestFailed", ExternalId: lb.Vip, Reason: "x"));

        Assert.Equal(CurrentExistence.Exists, lb.CurrentExistence);
        Assert.Null(lb.RequestFailureReason);
        Assert.Equal(new[] { "load_balancer.created" }, _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Observed_Absent_IsNotFound_AndDeleted_RecordsTheOutcome()
    {
        Network network = NetworkWithIpv6();
        LoadBalancer lb = await OnIncusAsync(network, VmOn(network));
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
        _activity.Events.Clear();

        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Observed", Exists: false));
        Assert.Equal(CurrentExistence.NotFound, lb.CurrentExistence);
        Assert.Equal(LoadBalancerTransition.Deleting, lb.Transition);
        await StatusHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Deleted"));

        Assert.True(lb.IsTerminal);
        Assert.Equal(new[] { "load_balancer.deleted" }, _activity.Events.Select(e => e.Action));
    }

    [Theory]
    [InlineData("Observed", null, null)]
    [InlineData("Created", null, null)]
    [InlineData("Updated", null, null)]
    [InlineData("Running", null, null)]
    public async Task AMalformedReport_IsRefused_BeforeLoading(string report, bool? exists, string? externalId)
    {
        string code = await CodeOf(() => StatusHandler().HandleAsync(
            new UpdateLoadBalancerStatusCommand(Guid.NewGuid(), report, exists, externalId)));

        Assert.Equal("INVALID_STATUS", code);
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
        gone.RequestDelete();
        gone.MarkDeleted();

        List<LoadBalancerQueryResult> rows = await new ListLoadBalancersQueryHandler(_repo, _currentUser.Object)
            .HandleAsync(new ListLoadBalancersQuery());

        LoadBalancerQueryResult row = Assert.Single(rows);
        Assert.Equal(kept.Id, row.Id);
        Assert.Equal(new[] { "TCP 80->8080", "UDP 53->53" }, row.Listeners.Select(l => $"{l.Protocol} {l.Port}->{l.TargetPort}"));
        Assert.Equal(new[] { a.Id }, row.Backends.Select(b => b.VmId));
        Assert.Equal(LoadBalancerTransition.Updating, row.Transition);
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
