using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops features/load-balancer/design.md#lb-lifecycle: existence only, one request waiting at a time, observations
// converge and end nothing. Desired and applied listeners/backends share rows (titan-ops features/load-balancer/design.md#lb-resource-model):
// a give-up of an update needs what Incus last took (titan-ops architecture/desired-and-current.md#abandoned-request).
// MarkCreated, MarkUpdated, MarkDeleted and MarkRequestFailed here are replays of the commands' reports
// (Fakes/ProvisionedOnTheProvider); how a request ends is LoadBalancerRequestLifecycleTests'.
public class LoadBalancerStateMachineTests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private static readonly Tenant Owner = TestTenant.WithRoomToSpare();
    private static readonly Guid NetworkId = Guid.NewGuid();
    private static readonly LoadBalancerListenerSpec Http = new(LoadBalancerProtocol.TCP, 80, 8080);
    private static readonly LoadBalancerListenerSpec Dns = new(LoadBalancerProtocol.UDP, 53, 53);

    private static Vm VmOnIncus(Guid? networkId = null, Titan.Domain.Aggregates.Tenant.Tenant? owner = null)
    {
        var vm = Vm.Create((owner ?? Owner).Id, networkId ?? NetworkId, $"vm-{Guid.NewGuid():N}", "m", "tpl", "cluster-a", 20);
        vm.MarkRunning($"vm{Guid.NewGuid():N}"[..28], "10.0.0.5");
        return vm;
    }

    private static LoadBalancer NewLoadBalancer(params Vm[] backends) =>
        LoadBalancer.Create(Owner.Id, "web", NetworkId, "net-ext", Subnet, "cluster-a", LoadBalancerVip.For(Subnet, 1), [Http],
            backends.Length == 0 ? [VmOnIncus()] : backends);

    private static LoadBalancer OnIncus(params Vm[] backends)
    {
        LoadBalancer lb = NewLoadBalancer(backends);
        lb.MarkCreated(lb.Vip);
        return lb;
    }

    private static string[] Keys(IEnumerable<LoadBalancerListener> listeners) =>
        listeners.Select(l => $"{l.Protocol} {l.Port}->{l.TargetPort}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ---- create

    [Fact]
    public void Create_WaitsForIncus_WithTheAskedListenersAndBackends()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();

        LoadBalancer lb = NewLoadBalancer(a, b);

        Assert.Equal(CurrentExistence.NotCreated, lb.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, lb.DesiredExistence);
        Assert.Equal(LoadBalancerTransition.Creating, lb.Transition);
        Assert.Equal("2001:41d0:d00:d405::1:1", lb.Vip);
        Assert.Equal(1, lb.VipIndex);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.DesiredListeners));
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), lb.DesiredBackends.Select(x => x.VmId).OrderBy(x => x));
        Assert.Equal(a.ExternalId, lb.DesiredBackends.Single(x => x.VmId == a.Id).ExternalVmId);
        Assert.Empty(lb.AppliedListeners);
    }

    [Fact]
    public void Create_WithAnotherTenantsVm_IsRefused()
    {
        Vm foreign = VmOnIncus(owner: TestTenant.WithRoomToSpare());

        var ex = Assert.Throws<BusinessException>(() => NewLoadBalancer(foreign));

        Assert.Equal("LOAD_BALANCER_BACKEND_INVALID", ex.Code);
    }

    [Fact]
    public void Create_WithAVmOfAnotherNetwork_IsRefused()
    {
        var ex = Assert.Throws<BusinessException>(() => NewLoadBalancer(VmOnIncus(networkId: Guid.NewGuid())));

        Assert.Equal("LOAD_BALANCER_BACKEND_INVALID", ex.Code);
    }

    [Fact]
    public void Create_WithAVmIncusDoesNotHaveYet_IsRefused()
    {
        var vm = Vm.Create(Owner.Id, NetworkId, "vm-new", "m", "tpl", "cluster-a", 20);

        var ex = Assert.Throws<BusinessException>(() => NewLoadBalancer(vm));

        Assert.Equal("LOAD_BALANCER_BACKEND_INVALID", ex.Code);
    }

    [Fact]
    public void Create_WithAVmWhoseDeleteWasPressed_IsRefused()
    {
        Vm vm = VmOnIncus();
        vm.Delete();

        var ex = Assert.Throws<BusinessException>(() => NewLoadBalancer(vm));

        Assert.Equal("LOAD_BALANCER_BACKEND_INVALID", ex.Code);
    }

    public static TheoryData<string, LoadBalancerListenerSpec[], int> BadSpecs => new()
    {
        { "no listener", [], 1 },
        { "eleven listeners", Enumerable.Range(1, 11).Select(p => new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, p, p)).ToArray(), 1 },
        { "same protocol and port twice", [Http, new(LoadBalancerProtocol.TCP, 80, 9090)], 1 },
        { "port 0", [new(LoadBalancerProtocol.TCP, 0, 8080)], 1 },
        { "target port 65536", [new(LoadBalancerProtocol.TCP, 80, 65536)], 1 },
        { "no backend", [Http], 0 },
        { "twenty-one backends", [Http], 21 },
    };

    [Theory]
    [MemberData(nameof(BadSpecs))]
    public void Create_WithASpecOutsideTheRules_IsRefused(string why, LoadBalancerListenerSpec[] listeners, int backendCount)
    {
        Vm[] vms = Enumerable.Range(0, backendCount).Select(_ => VmOnIncus()).ToArray();

        var ex = Assert.Throws<BusinessException>(() => LoadBalancer.Create(
            Owner.Id, "web", NetworkId, "net-ext", Subnet, "cluster-a", LoadBalancerVip.For(Subnet, 1), listeners, vms));

        Assert.True(ex.Code == "INVALID_LOAD_BALANCER_SPEC", why);
    }

    [Fact]
    public void Create_TheSameVmTwice_IsRefused()
    {
        Vm vm = VmOnIncus();

        var ex = Assert.Throws<BusinessException>(() => NewLoadBalancer(vm, vm));

        Assert.Equal("INVALID_LOAD_BALANCER_SPEC", ex.Code);
    }

    [Fact]
    public void TheSamePortOnTcpAndUdp_IsFine()
    {
        LoadBalancer lb = LoadBalancer.Create(Owner.Id, "dns", NetworkId, "net-ext", Subnet, "cluster-a", LoadBalancerVip.For(Subnet, 1),
            [Dns, new(LoadBalancerProtocol.TCP, 53, 53)], [VmOnIncus()]);

        Assert.Equal(2, lb.DesiredListeners.Count());
    }

    // ---- create reports

    [Fact]
    public void Created_FulfilsTheCreateOnce_AndAppliesTheAsked()
    {
        LoadBalancer lb = NewLoadBalancer();

        Assert.True(lb.MarkCreated(lb.Vip));
        Assert.False(lb.MarkCreated("another"));

        Assert.Equal(CurrentExistence.Exists, lb.CurrentExistence);
        Assert.Null(lb.Transition);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
    }

    [Fact]
    public void CreateGivesUp_BeforeIncusHadIt_EndsTheCreate_AndCountsNoMore()
    {
        LoadBalancer lb = NewLoadBalancer();

        lb.MarkRequestFailed("Load balancer creation failed.", "detail");

        Assert.Null(lb.Transition);
        Assert.Equal("Load balancer creation failed.", lb.OperationFailureReason);
        Assert.Equal(CurrentExistence.NotCreated, lb.CurrentExistence);
    }

    // ---- update

    [Fact]
    public void Update_WaitsForIncus_AndKeepsWhatIncusHasAsApplied()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();
        LoadBalancer lb = OnIncus(a);

        OperationTicket update = lb.Update([Http, Dns], [a, b]);

        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
        Assert.Equal((LoadBalancerOperationKind.Update, update.OperationId), (lb.Pending!.Kind, lb.Pending.Id));
        Assert.Equal(new[] { "TCP 80->8080", "UDP 53->53" }, Keys(lb.DesiredListeners));
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.AppliedListeners));
        Assert.Equal(2, lb.DesiredBackends.Count());
        Assert.Single(lb.AppliedBackends);
    }

    // A listener asked again is the same row, not a second one with the same key.
    [Fact]
    public void Update_KeepsTheRowOfAListenerAskedAgain()
    {
        LoadBalancer lb = OnIncus();
        Guid httpRow = lb.Listeners.Single().Id;

        lb.Update([Http, Dns], [VmOnIncus()]);

        Assert.Equal(2, lb.Listeners.Count);
        Assert.Equal(httpRow, lb.Listeners.Single(l => l.Port == 80).Id);
    }

    [Fact]
    public void Updated_ForTheWaitingRequest_AppliesTheNewSpec()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.Update([Dns], [b]).OperationId;

        Assert.True(lb.MarkUpdated(update));

        Assert.Null(lb.Transition);
        Assert.Equal(new[] { "UDP 53->53" }, Keys(lb.Listeners));
        Assert.Equal(new[] { b.Id }, lb.Backends.Select(x => x.VmId));
        Assert.All(lb.Listeners, l => Assert.True(l.IsApplied && l.IsDesired));
    }

    [Fact]
    public void Updated_ForAnotherRequest_ChangesNothing()
    {
        LoadBalancer lb = OnIncus();
        lb.Update([Dns], [VmOnIncus()]);

        Assert.False(lb.MarkUpdated(Guid.NewGuid()));

        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.AppliedListeners));
    }

    [Fact]
    public void UpdateGivesUp_PutsTheDesiredBackToWhatIncusHolds()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.Update([Dns], [VmOnIncus()]).OperationId;

        lb.FailOperation(update, null, "Load balancer update failed.", "detail");

        Assert.Null(lb.Transition);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.DesiredListeners));
        Assert.Equal(new[] { a.Id }, lb.DesiredBackends.Select(x => x.VmId));
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.Listeners));
        Assert.Equal("Load balancer update failed.", lb.OperationFailureReason);
    }

    [Fact]
    public void Update_WhileARequestWaits_IsRefused()
    {
        LoadBalancer creating = NewLoadBalancer();
        LoadBalancer updating = OnIncus();
        updating.Update([Dns], [VmOnIncus()]);

        Assert.Equal("LOAD_BALANCER_INVALID_STATE",
            Assert.Throws<BusinessException>(() => creating.Update([Dns], [VmOnIncus()])).Code);
        Assert.Equal("LOAD_BALANCER_INVALID_STATE",
            Assert.Throws<BusinessException>(() => updating.Update([Http], [VmOnIncus()])).Code);
    }

    // The handler answers a repeat itself (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    [Fact]
    public void Update_ToWhatIsAlreadyAsked_IsRefusedByTheAggregate_AndSeenByHasDesiredSpec()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);

        Assert.True(lb.HasDesiredSpec([Http], [a.Id]));
        Assert.False(lb.HasDesiredSpec([Http, Dns], [a.Id]));
        Assert.False(lb.HasDesiredSpec([new(LoadBalancerProtocol.TCP, 80, 9090)], [a.Id]));
        Assert.False(lb.HasDesiredSpec([Http], [Guid.NewGuid()]));
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => lb.Update([Http], [a])).Code);
    }

    // ---- delete

    [Fact]
    public void Delete_WaitsForTheCommand_AndHandsOutTheVip()
    {
        LoadBalancer lb = OnIncus();

        string vip = lb.Delete()!.ProviderId;

        Assert.Equal(lb.Vip, vip);
        Assert.Equal(LoadBalancerTransition.Deleting, lb.Transition);
        Assert.Equal(CurrentExistence.Exists, lb.CurrentExistence);
    }

    [Fact]
    public void Delete_WinsOverAWaitingUpdate()
    {
        LoadBalancer lb = OnIncus();
        Guid update = lb.Update([Dns], [VmOnIncus()]).OperationId;

        lb.Delete();

        Assert.Equal(LoadBalancerTransition.Deleting, lb.Transition);
        Assert.NotEqual(update, lb.Pending!.Id);
        Assert.False(lb.MarkUpdated(update));
        Assert.Equal(LoadBalancerTransition.Deleting, lb.Transition);
    }

    [Fact]
    public void Delete_WhileTheCreateWaits_OrARepeat_IsRefused()
    {
        LoadBalancer creating = NewLoadBalancer();
        LoadBalancer deleting = OnIncus();
        deleting.Delete();
        LoadBalancer gone = OnIncus();
        gone.Delete();
        gone.MarkDeleted();

        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => creating.Delete()).Code);
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => deleting.Delete()).Code);
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => gone.Delete()).Code);
    }

    [Fact]
    public void Deleted_FulfilsTheDelete_AndAbsorbsEverythingAfter()
    {
        LoadBalancer lb = OnIncus();
        lb.Delete();

        Assert.True(lb.MarkDeleted());
        Assert.False(lb.MarkDeleted());
        lb.MarkObserved();
        lb.MarkNotFound();
        lb.MarkCreated(lb.Vip);

        Assert.True(lb.IsTerminal);
        Assert.Equal(CurrentExistence.Deleted, lb.CurrentExistence);
        Assert.Null(lb.Transition);
    }

    [Fact]
    public void DeleteGivesUp_PutsTheDesiredExistenceBack()
    {
        LoadBalancer lb = OnIncus();
        Guid delete = lb.Delete()!.OperationId;

        lb.FailOperation(delete, null, "Load balancer deletion failed.", "detail");

        Assert.Null(lb.Transition);
        Assert.Equal(DesiredExistence.Exists, lb.DesiredExistence);
        Assert.Equal("Load balancer deletion failed.", lb.OperationFailureReason);
    }

    [Fact]
    public void RequestDelete_DeletesHere_OnlyForACreateThatGaveUpBeforeIncus()
    {
        LoadBalancer failed = NewLoadBalancer();
        failed.MarkRequestFailed("Load balancer creation failed.", null);

        Assert.Null(failed.Delete());

        Assert.True(failed.IsTerminal);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.NotNull(OnIncus().Delete());
        Assert.Equal("LOAD_BALANCER_INVALID_STATE",
            Assert.Throws<BusinessException>(() => NewLoadBalancer().Delete()).Code);
    }

    // ---- reconciler

    // Absent is NotFound, never Deleted: only Titan's own delete command says that
    // (titan-ops architecture/desired-and-current.md#existence-axis).
    [Fact]
    public void Observed_AfterNotFound_IsBackOnIncus()
    {
        LoadBalancer lb = OnIncus();
        lb.MarkNotFound();

        lb.MarkObserved();

        Assert.Equal(CurrentExistence.Exists, lb.CurrentExistence);
    }

    // Only the create command's own report ends the create (titan-ops architecture/resource-lifecycle.md#invariants).
    [Fact]
    public void NotFound_KeepsAWaitingDelete_AndAWaitingCreate()
    {
        LoadBalancer deleting = OnIncus();
        deleting.Delete();
        LoadBalancer creating = NewLoadBalancer();

        deleting.MarkNotFound();
        creating.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, deleting.CurrentExistence);
        Assert.Equal(LoadBalancerTransition.Deleting, deleting.Transition);
        Assert.Equal(CurrentExistence.NotFound, creating.CurrentExistence);
        Assert.Equal(LoadBalancerTransition.Creating, creating.Transition);
    }

    [Fact]
    public void Observation_WhileADeleteOrUpdateWaits_WithdrawsNothing()
    {
        LoadBalancer deleting = OnIncus();
        deleting.Delete();
        LoadBalancer updating = OnIncus();
        updating.Update([Dns], [VmOnIncus()]);

        deleting.MarkObserved();
        updating.MarkObserved();

        Assert.Equal(LoadBalancerTransition.Deleting, deleting.Transition);
        Assert.Equal(LoadBalancerTransition.Updating, updating.Transition);
    }

    // ---- rename (titan-ops features/load-balancer/design.md#lb-rename)

    [Fact]
    public void Rename_OnIncusOrWhileItsCreateOrUpdateWaits_ChangesOnlyTheName()
    {
        LoadBalancer onIncus = OnIncus();
        LoadBalancer creating = NewLoadBalancer();
        LoadBalancer updating = OnIncus();
        updating.Update([Http, Dns], [VmOnIncus()]);
        string vip = onIncus.Vip;

        onIncus.Rename("api");
        creating.Rename("dns");
        updating.Rename("www");

        Assert.Equal("api", onIncus.Name);
        Assert.Equal(vip, onIncus.Vip);
        Assert.Null(onIncus.Transition);
        Assert.Equal("dns", creating.Name);
        Assert.Equal(LoadBalancerTransition.Creating, creating.Transition);
        Assert.Equal("www", updating.Name);
        Assert.Equal(LoadBalancerTransition.Updating, updating.Transition);
    }

    [Fact]
    public void Rename_WhileItsDeleteWaitsOrOnceDeleted_IsInvalidState()
    {
        LoadBalancer deleting = OnIncus();
        deleting.Delete();
        LoadBalancer deleted = OnIncus();
        deleted.Delete();
        deleted.MarkDeleted();

        var whileDeleting = Assert.Throws<BusinessException>(() => deleting.Rename("api"));
        var onceDeleted = Assert.Throws<BusinessException>(() => deleted.Rename("api"));

        Assert.Equal("LOAD_BALANCER_INVALID_STATE", whileDeleting.Code);
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", onceDeleted.Code);
        Assert.Equal("web", deleting.Name);
    }
}
