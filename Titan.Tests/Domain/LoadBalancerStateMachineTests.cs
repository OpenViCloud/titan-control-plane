using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops features/load-balancer/design.md#lb-lifecycle: one status, one operation waiting at a time, observations
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

        Assert.Equal(LoadBalancerStatus.Creating, lb.Status);
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
        vm.Delete(Guid.NewGuid());

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

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
    }

    [Fact]
    public void CreateGivesUp_BeforeIncusHadIt_EndsTheCreate_AndCountsNoMore()
    {
        LoadBalancer lb = NewLoadBalancer();

        lb.MarkRequestFailed("Load balancer creation failed.", "detail");

        Assert.Equal(LoadBalancerStatus.Failed, lb.Status);
        Assert.Equal("Load balancer creation failed.", lb.Operation.FailureReason);
        Assert.False(lb.OccupiesQuota);
    }

    // ---- update

    [Fact]
    public void Update_WaitsForIncus_AndKeepsWhatIncusHasAsApplied()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();
        LoadBalancer lb = OnIncus(a);

        OperationTicket update = lb.Update([Http, Dns], [a, b]);

        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
        Assert.Equal(update.OperationId, lb.Operation.PendingId);
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

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
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

        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.AppliedListeners));
    }

    [Fact]
    public void UpdateGivesUp_PutsTheDesiredBackToWhatIncusHolds()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.Update([Dns], [VmOnIncus()]).OperationId;

        lb.UpdateFailed(update, null, "Load balancer update failed.", "detail");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.DesiredListeners));
        Assert.Equal(new[] { a.Id }, lb.DesiredBackends.Select(x => x.VmId));
        Assert.Equal(new[] { "TCP 80->8080" }, Keys(lb.Listeners));
        Assert.Equal("Load balancer update failed.", lb.Operation.FailureReason);
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
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        Assert.Equal(LoadBalancerStatus.Available, lb.Operation.LastSeenStatus);
    }

    [Fact]
    public void Delete_WinsOverAWaitingUpdate()
    {
        LoadBalancer lb = OnIncus();
        Guid update = lb.Update([Dns], [VmOnIncus()]).OperationId;

        lb.Delete();

        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        Assert.NotEqual(update, lb.Operation.PendingId);
        Assert.False(lb.MarkUpdated(update));
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
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

        Assert.True(lb.IsDeleted);
        Assert.Equal(LoadBalancerStatus.Deleted, lb.Status);
    }

    [Fact]
    public void DeleteGivesUp_PutsTheLoadBalancerBackAsIncusHeldIt()
    {
        LoadBalancer lb = OnIncus();
        Guid delete = lb.Delete()!.OperationId;

        lb.DeleteFailed(delete, null, "Load balancer deletion failed.", "detail");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.True(lb.OccupiesQuota);
        Assert.Equal("Load balancer deletion failed.", lb.Operation.FailureReason);
    }

    [Fact]
    public void RequestDelete_DeletesHere_OnlyForACreateThatGaveUpBeforeIncus()
    {
        LoadBalancer failed = NewLoadBalancer();
        failed.MarkRequestFailed("Load balancer creation failed.", null);

        Assert.Null(failed.Delete());

        Assert.True(failed.IsDeleted);
        Assert.Equal(LoadBalancerStatus.Deleted, failed.Status);
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

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
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

        Assert.Equal(LoadBalancerStatus.Deleting, deleting.Status);
        Assert.Null(deleting.InstanceStatus);
        Assert.Equal(LoadBalancerStatus.Creating, creating.Status);
        Assert.Null(creating.InstanceStatus);
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

        Assert.Equal(LoadBalancerStatus.Deleting, deleting.Status);
        Assert.Equal(LoadBalancerStatus.Updating, updating.Status);
    }

    // ---- the one status (titan-ops features/load-balancer/design.md#lb-lifecycle): every situation a load balancer can be in

    public static TheoryData<string> Situations => new()
    {
        "Creating", "Available", "UpdatingSeen", "UpdatingLost", "DeletingSeen", "DeletingLost", "NotFound", "Failed", "Deleted",
    };

    private static LoadBalancer InSituation(string situation)
    {
        Vm backend = VmOnIncus();
        switch (situation)
        {
            case "Creating":
                return NewLoadBalancer(backend);
            case "Available":
                return OnIncus(backend);
            case "UpdatingSeen":
            {
                LoadBalancer lb = OnIncus(backend);
                lb.Update([Http, Dns], [backend]);
                return lb;
            }
            case "UpdatingLost":
            {
                LoadBalancer lb = OnIncus(backend);
                lb.Update([Http, Dns], [backend]);
                lb.MarkNotFound();
                return lb;
            }
            case "DeletingSeen":
            {
                LoadBalancer lb = OnIncus(backend);
                lb.Delete();
                return lb;
            }
            case "DeletingLost":
            {
                LoadBalancer lb = OnIncus(backend);
                lb.MarkNotFound();
                lb.Delete();
                return lb;
            }
            case "NotFound":
            {
                LoadBalancer lb = OnIncus(backend);
                lb.MarkNotFound();
                return lb;
            }
            case "Failed":
            {
                LoadBalancer lb = NewLoadBalancer(backend);
                lb.MarkRequestFailed("Load balancer creation failed.", null);
                return lb;
            }
            case "Deleted":
            {
                LoadBalancer lb = OnIncus(backend);
                lb.Delete();
                lb.MarkDeleted();
                return lb;
            }
            default:
                throw new ArgumentException(situation);
        }
    }

    [Theory]
    [InlineData("Creating", LoadBalancerStatus.Creating, null)]
    [InlineData("Available", LoadBalancerStatus.Available, LoadBalancerStatus.Available)]
    [InlineData("UpdatingSeen", LoadBalancerStatus.Updating, LoadBalancerStatus.Available)]
    [InlineData("UpdatingLost", LoadBalancerStatus.Updating, null)]
    [InlineData("DeletingSeen", LoadBalancerStatus.Deleting, LoadBalancerStatus.Available)]
    [InlineData("DeletingLost", LoadBalancerStatus.Deleting, null)]
    [InlineData("NotFound", LoadBalancerStatus.NotFound, null)]
    [InlineData("Failed", LoadBalancerStatus.Failed, null)]
    [InlineData("Deleted", LoadBalancerStatus.Deleted, null)]
    public void EverySituation_HasItsStatus_AndWhetherIncusHoldsIt(string situation, LoadBalancerStatus status, LoadBalancerStatus? held)
    {
        LoadBalancer lb = InSituation(situation);

        Assert.Equal(status, lb.Status);
        Assert.Equal(held, lb.InstanceStatus);
    }

    // The quota counts what Incus holds or is about to (titan-ops features/load-balancer/design.md#lb-quota-and-billing).
    [Theory]
    [InlineData("Creating", true)]
    [InlineData("Available", true)]
    [InlineData("UpdatingSeen", true)]
    [InlineData("UpdatingLost", false)]
    [InlineData("DeletingSeen", false)]
    [InlineData("DeletingLost", false)]
    [InlineData("NotFound", false)]
    [InlineData("Failed", false)]
    [InlineData("Deleted", false)]
    public void OccupiesQuota_CountsWhatIncusHoldsOrIsAboutTo(string situation, bool counts)
    {
        Assert.Equal(counts, InSituation(situation).OccupiesQuota);
    }

    [Theory]
    [InlineData("Creating", false, false)]
    [InlineData("Available", false, false)]
    [InlineData("UpdatingSeen", false, false)]
    [InlineData("DeletingSeen", false, true)]
    [InlineData("NotFound", false, false)]
    [InlineData("Failed", false, false)]
    [InlineData("Deleted", true, true)]
    public void IsDeleted_AndIsDeletedOrDeleting_ReadTheStatus(string situation, bool deleted, bool deletedOrDeleting)
    {
        LoadBalancer lb = InSituation(situation);

        Assert.Equal(deleted, lb.IsDeleted);
        Assert.Equal(deletedOrDeleting, lb.IsDeletedOrDeleting);
    }

    // An update starts only from Available: Incus holds the load balancer and nothing waits.
    [Theory]
    [MemberData(nameof(Situations))]
    public void Update_StartsOnlyFromAvailable(string situation)
    {
        LoadBalancer lb = InSituation(situation);
        LoadBalancerStatus before = lb.Status;

        if (situation == "Available")
        {
            lb.Update([Dns], [VmOnIncus()]);
            Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
            return;
        }

        var refused = Assert.Throws<BusinessException>(() => lb.Update([Dns], [VmOnIncus()]));

        Assert.Equal("LOAD_BALANCER_INVALID_STATE", refused.Code);
        Assert.Equal(before, lb.Status);
    }

    // A delete is allowed from a load balancer Incus has (or had), including the one whose update waits; it is refused for
    // one whose create waits, one already going, and one gone. One that never reached Incus is deleted at once.
    [Theory]
    [InlineData("Creating", "refused")]
    [InlineData("Available", "asked")]
    [InlineData("UpdatingSeen", "asked")]
    [InlineData("UpdatingLost", "asked")]
    [InlineData("DeletingSeen", "refused")]
    [InlineData("DeletingLost", "refused")]
    [InlineData("NotFound", "asked")]
    [InlineData("Failed", "here")]
    [InlineData("Deleted", "refused")]
    public void Delete_IsAskedRefusedOrDoneHere_BySituation(string situation, string outcome)
    {
        LoadBalancer lb = InSituation(situation);
        LoadBalancerStatus held = lb.InstanceStatus ?? LoadBalancerStatus.NotFound;

        if (outcome == "refused")
        {
            Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => lb.Delete()).Code);
            return;
        }

        OperationTicket? ticket = lb.Delete();

        if (outcome == "here")
        {
            Assert.Null(ticket);
            Assert.Equal(LoadBalancerStatus.Deleted, lb.Status);
            Assert.Null(lb.Operation.PendingId);
            return;
        }
        Assert.NotNull(ticket);
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        Assert.Equal(ticket.OperationId, lb.Operation.PendingId);
        Assert.Equal(held == LoadBalancerStatus.Available ? LoadBalancerStatus.Available : (LoadBalancerStatus?)null, lb.Operation.LastSeenStatus);
    }

    // Deleting replaces the waiting update, and the update's late report then names an operation no longer waiting.
    [Fact]
    public void Delete_OfAnUpdatingLoadBalancer_ReplacesTheUpdate_AndTheLateReportsAreIgnored()
    {
        LoadBalancer lb = InSituation("UpdatingSeen");
        Guid update = lb.Operation.PendingId!.Value;

        OperationTicket delete = lb.Delete()!;

        Assert.NotEqual(update, delete.OperationId);
        Assert.False(lb.UpdateSucceeded(update, null));
        Assert.False(lb.UpdateFailed(update, null, "late", "late"));
        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        Assert.Equal(delete.OperationId, lb.Operation.PendingId);
        Assert.Null(lb.Operation.FailureReason);
    }

    // An observation at rest writes the status; while an operation waits it writes only what was seen; Failed and Deleted
    // take none.
    [Theory]
    [InlineData("Creating", "Creating", null)]
    [InlineData("Available", "Available", "Available")]
    [InlineData("UpdatingSeen", "Updating", "Available")]
    [InlineData("UpdatingLost", "Updating", "Available")]
    [InlineData("DeletingSeen", "Deleting", "Available")]
    [InlineData("DeletingLost", "Deleting", "Available")]
    [InlineData("NotFound", "Available", "Available")]
    [InlineData("Failed", "Failed", null)]
    [InlineData("Deleted", "Deleted", null)]
    public void MarkObserved_WritesTheStatusAtRest_OrWhatWasSeenWhileAnOperationWaits(string situation, string status, string? held)
    {
        LoadBalancer lb = InSituation(situation);

        lb.MarkObserved();

        Assert.Equal(Enum.Parse<LoadBalancerStatus>(status), lb.Status);
        // Creating is the one waiting situation that starts without a thing seen; observed, it has seen the load balancer.
        LoadBalancerStatus? expected = held == null ? null : Enum.Parse<LoadBalancerStatus>(held);
        if (situation == "Creating")
        {
            expected = LoadBalancerStatus.Available;
        }
        Assert.Equal(expected, lb.InstanceStatus);
    }

    [Theory]
    [InlineData("Creating", "Creating")]
    [InlineData("Available", "NotFound")]
    [InlineData("UpdatingSeen", "Updating")]
    [InlineData("UpdatingLost", "Updating")]
    [InlineData("DeletingSeen", "Deleting")]
    [InlineData("DeletingLost", "Deleting")]
    [InlineData("NotFound", "NotFound")]
    [InlineData("Failed", "Failed")]
    [InlineData("Deleted", "Deleted")]
    public void MarkNotFound_WritesTheStatusAtRest_OrWhatWasSeenWhileAnOperationWaits_NeverDeleted(string situation, string status)
    {
        LoadBalancer lb = InSituation(situation);

        lb.MarkNotFound();

        Assert.Equal(Enum.Parse<LoadBalancerStatus>(status), lb.Status);
        if (lb.Status is LoadBalancerStatus.Creating or LoadBalancerStatus.Updating or LoadBalancerStatus.Deleting)
        {
            Assert.Null(lb.Operation.LastSeenStatus);
        }
    }

    // A rename is refused only once the delete was asked.
    [Theory]
    [InlineData("Creating", false)]
    [InlineData("Available", false)]
    [InlineData("UpdatingSeen", false)]
    [InlineData("NotFound", false)]
    [InlineData("Failed", false)]
    [InlineData("DeletingSeen", true)]
    [InlineData("Deleted", true)]
    public void Rename_IsRefusedOnlyWhileDeletingOrOnceDeleted(string situation, bool refused)
    {
        LoadBalancer lb = InSituation(situation);

        if (refused)
        {
            Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => lb.Rename("api")).Code);
            Assert.Equal("web", lb.Name);
            return;
        }
        lb.Rename("api");
        Assert.Equal("api", lb.Name);
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
        Assert.Equal(LoadBalancerStatus.Available, onIncus.Status);
        Assert.Equal("dns", creating.Name);
        Assert.Equal(LoadBalancerStatus.Creating, creating.Status);
        Assert.Equal("www", updating.Name);
        Assert.Equal(LoadBalancerStatus.Updating, updating.Status);
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
