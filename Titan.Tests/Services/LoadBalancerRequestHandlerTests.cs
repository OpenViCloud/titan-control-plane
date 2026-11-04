using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The request routes of titan-ops architecture/resource-lifecycle.md#callback-routes for a load balancer, at the handler: a
// report ends the request it names and records its outcome once; an observation writes current and ends nothing; the
// row is locked. The old route's mapping onto the same shape follows, and the requests' own repeat and lock rules are
// at the end. LoadBalancerServiceTests keeps the rest of the old route.
public class LoadBalancerRequestHandlerTests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();
    private static readonly Guid NetworkId = Guid.NewGuid();
    private static readonly LoadBalancerListenerInput Http = new("TCP", 80, 8080);
    private static readonly LoadBalancerListenerInput Dns = new("UDP", 53, 53);

    // Only the locked reads are set up: a handler that read the load balancer unlocked would find nothing and answer 404.
    private readonly Mock<ILoadBalancerRepository> _repo = new();
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly List<Vm> _knownVms = new();

    public LoadBalancerRequestHandlerTests()
    {
        _currentUser.Setup(u => u.TenantId).Returns(Owner.Id);
        _vms.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _knownVms.FirstOrDefault(v => v.Id == id));
    }

    private EndLoadBalancerRequestCommandHandler EndHandler() => new(_repo.Object, _activity);
    private ObserveLoadBalancerCommandHandler ObserveHandler() => new(_repo.Object);
    private UpdateLoadBalancerStatusCommandHandler OldHandler() => new(_repo.Object, _activity);
    private UpdateLoadBalancerCommandHandler UpdateHandler() =>
        new(_repo.Object, _vms.Object, _currentUser.Object, _commands.Object, _activity);
    private DeleteLoadBalancerCommandHandler DeleteHandler() =>
        new(_repo.Object, _currentUser.Object, _commands.Object, _activity);

    private LoadBalancer Given(LoadBalancer lb)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(lb.Id, It.IsAny<CancellationToken>())).ReturnsAsync(lb);
        return lb;
    }

    private Vm VmOnIncus()
    {
        Vm vm = Vm.Create(Owner.Id, NetworkId, $"vm-{_knownVms.Count}", "m", "tpl", "cluster-1", 20);
        vm.MarkRunning($"vm{_knownVms.Count:D26}", "10.0.0.5");
        _knownVms.Add(vm);
        return vm;
    }

    private LoadBalancer Creating(Vm? backend = null) =>
        Given(LoadBalancer.Create(Owner.Id, "web", NetworkId, "net-ext", Subnet, "cluster-1", LoadBalancerVip.For(Subnet, 1),
            [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [backend ?? VmOnIncus()]));

    private LoadBalancer OnIncus(Vm? backend = null)
    {
        LoadBalancer lb = Creating(backend);
        lb.MarkCreated(lb.Vip);
        return lb;
    }

    private static Guid Waiting(LoadBalancer lb) => lb.Pending!.Id;

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    private void NothingSaved() => _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);

    private static string Shape(LoadBalancer lb) =>
        $"{lb.CurrentExistence}/{lb.DesiredExistence}/{(lb.Transition == null ? "-" : lb.Transition.Value.ToString())}";

    private Task End(LoadBalancer lb, Guid requestId, string outcome, string? externalId = null) =>
        EndHandler().HandleAsync(new EndLoadBalancerRequestCommand(lb.Id, requestId, outcome, externalId,
            outcome == "Failed" ? "The load balancer request could not be completed." : null, outcome == "Failed" ? "raw" : null));

    // ---------- the request report ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheVip_RecordsLoadBalancerCreatedOnce_AndSaves()
    {
        LoadBalancer lb = Creating();
        Guid create = Waiting(lb);

        await End(lb, create, "Done", lb.Vip);
        await End(lb, create, "Done", lb.Vip);

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Single(lb.AppliedListeners);
        Assert.Equal(["load_balancer.created"], Outcomes());
        Assert.Equal(Owner.Id, _activity.Events.Single().OutcomeTenantId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Done_OfTheCreate_WithoutTheVip_Is422_AndSavesNothing(string? externalId)
    {
        LoadBalancer lb = Creating();

        BusinessException refused = await Assert.ThrowsAsync<BusinessException>(() => End(lb, Waiting(lb), "Done", externalId));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(LoadBalancerTransition.Creating, lb.Transition);
        NothingSaved();
    }

    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsAVipIncusHas_AndRecordsNoOutcome()
    {
        LoadBalancer lb = Creating();

        await End(lb, Waiting(lb), "Failed", lb.Vip);

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal("The load balancer request could not be completed.", lb.RequestFailureReason);
        Assert.Equal("raw", lb.RequestFailureDetail);
        Assert.Empty(Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheUpdate_AppliesIt_AndRecordsLoadBalancerUpdatedOnce()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.RequestUpdate([new(LoadBalancerProtocol.UDP, 53, 53)], [a]).RequestId;

        await End(lb, update, "Done");
        await End(lb, update, "Done");

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal([53], lb.AppliedListeners.Select(l => l.Port));
        Assert.Equal(["load_balancer.updated"], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheUpdate_AsksAgainForWhatIncusTook_AndRecordsUpdateFailedOnce()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.RequestUpdate([new(LoadBalancerProtocol.UDP, 53, 53)], [a]).RequestId;

        await End(lb, update, "Failed");
        await End(lb, update, "Failed");

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal([80], lb.DesiredListeners.Select(l => l.Port));
        Assert.Equal("The load balancer request could not be completed.", lb.RequestFailureReason);
        Assert.Equal(["load_balancer.update_failed"], Outcomes());
    }

    [Fact]
    public async Task Done_OfTheDelete_EndsTheLoadBalancer_AndRecordsLoadBalancerDeletedOnce()
    {
        LoadBalancer lb = OnIncus();
        Guid delete = lb.RequestDelete()!.RequestId;

        await End(lb, delete, "Done");
        await End(lb, delete, "Done");

        Assert.Equal("Deleted/Deleted/-", Shape(lb));
        Assert.Equal(["load_balancer.deleted"], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheDelete_GivesTheLoadBalancerBack_AndRecordsDeleteFailedOnce()
    {
        LoadBalancer lb = OnIncus();
        Guid delete = lb.RequestDelete()!.RequestId;

        await End(lb, delete, "Failed");
        await End(lb, delete, "Failed");

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal("The load balancer request could not be completed.", lb.RequestFailureReason);
        Assert.Equal(["load_balancer.delete_failed"], Outcomes());
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        LoadBalancer lb = OnIncus();
        lb.RequestDelete();

        await End(lb, Guid.NewGuid(), "Done");
        await End(lb, Guid.NewGuid(), "Failed");

        Assert.Equal("Exists/Deleted/Deleting", Shape(lb));
        Assert.Empty(Outcomes());
        NothingSaved();
    }

    [Fact]
    public async Task AReport_WithAnUnknownOutcome_Is400_BeforeTheLoadBalancerIsRead()
    {
        BadRequestException refused = await Assert.ThrowsAsync<BadRequestException>(() => EndHandler().HandleAsync(
            new EndLoadBalancerRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Updated")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("observation")]
    public async Task AReport_ForALoadBalancerThatIsNotThere_IsLoadBalancerNotFound(string route)
    {
        Func<Task> act = route == "requests"
            ? () => EndHandler().HandleAsync(new EndLoadBalancerRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done", "vip"))
            : () => ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(Guid.NewGuid(), true));

        NotFoundException missing = await Assert.ThrowsAsync<NotFoundException>(act);

        Assert.Equal("LOAD_BALANCER_NOT_FOUND", missing.Code);
    }

    // ---------- the observation ----------

    [Fact]
    public async Task AnObservation_WritesExistence_AndEveryWaitingRequestKeepsWaiting()
    {
        LoadBalancer creating = Creating();
        LoadBalancer deleting = OnIncus();
        deleting.RequestDelete();

        await ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(creating.Id, false));
        await ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(deleting.Id, false));

        Assert.Equal("NotFound/Exists/Creating", Shape(creating));
        Assert.Equal("NotFound/Deleted/Deleting", Shape(deleting));
        Assert.Empty(Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));

        await ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(deleting.Id, true));
        Assert.Equal("Exists/Deleted/Deleting", Shape(deleting));
    }

    // ---------- the old route, mapped onto the waiting request until rollout phase 6 ----------

    [Fact]
    public async Task OldRoute_Created_IsTheCreatesDone_AndRecordsLoadBalancerCreatedOnce()
    {
        LoadBalancer lb = Creating();

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Created", ExternalId: lb.Vip));
        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Created", ExternalId: lb.Vip));

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Single(lb.AppliedListeners);
        Assert.Equal(["load_balancer.created"], Outcomes());
    }

    // Only the create's own report ends it (titan-ops architecture/resource-lifecycle.md#invariants).
    [Fact]
    public async Task OldRoute_Observed_WhileTheCreateWaits_EndsNothing()
    {
        LoadBalancer lb = Creating();

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Observed", Exists: false));

        Assert.Equal("NotFound/Exists/Creating", Shape(lb));
        Assert.Empty(Outcomes());
    }

    [Fact]
    public async Task OldRoute_Updated_OfTheWaitingUpdatesId_IsItsDone_AnyOtherIdIsIgnored()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.RequestUpdate([new(LoadBalancerProtocol.UDP, 53, 53)], [a]).RequestId;

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Updated", IntentId: Guid.NewGuid()));
        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Updated", IntentId: update));

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal([53], lb.AppliedListeners.Select(l => l.Port));
        Assert.Equal(["load_balancer.updated"], Outcomes());
    }

    // An update's Updated arriving after a delete replaced it names a request no longer waiting.
    [Fact]
    public async Task OldRoute_UpdatedOfAReplacedUpdate_LeavesTheDeleteWaiting()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Guid update = lb.RequestUpdate([new(LoadBalancerProtocol.UDP, 53, 53)], [a]).RequestId;
        lb.RequestDelete();

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Updated", IntentId: update));

        Assert.Equal("Exists/Deleted/Deleting", Shape(lb));
        Assert.Empty(Outcomes());
    }

    [Fact]
    public async Task OldRoute_RequestFailed_EndsWhateverWaits_Failed_ButAnUpdateGiveUpOfAnotherIntentIsIgnored()
    {
        Vm a = VmOnIncus();
        LoadBalancer creating = Creating(a);
        LoadBalancer updating = OnIncus(a);
        Guid update = updating.RequestUpdate([new(LoadBalancerProtocol.UDP, 53, 53)], [a]).RequestId;
        LoadBalancer deleting = OnIncus(a);
        deleting.RequestDelete();

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(updating.Id, "RequestFailed", IntentId: Guid.NewGuid(),
            Reason: "stale"));
        Assert.Equal(LoadBalancerTransition.Updating, updating.Transition);
        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(deleting.Id, "RequestFailed", IntentId: update,
            Reason: "stale"));
        Assert.Equal(LoadBalancerTransition.Deleting, deleting.Transition);

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(creating.Id, "RequestFailed", Reason: "r", Detail: "raw"));
        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(updating.Id, "RequestFailed", IntentId: update,
            Reason: "r", Detail: "raw"));
        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(deleting.Id, "RequestFailed", Reason: "r", Detail: "raw"));

        Assert.All(new[] { creating, updating, deleting }, lb => Assert.Null(lb.Pending));
        Assert.All(new[] { creating, updating, deleting }, lb => Assert.Equal("raw", lb.RequestFailureDetail));
        Assert.Equal(DesiredExistence.Exists, deleting.DesiredExistence);
        Assert.Equal([80], updating.DesiredListeners.Select(l => l.Port));
        Assert.Equal(["load_balancer.update_failed", "load_balancer.delete_failed"], Outcomes());
    }

    [Fact]
    public async Task OldRoute_Deleted_WithADeleteWaiting_IsTheDeletesDone_AndWithNothingWaitingIsNotFound()
    {
        LoadBalancer deleting = OnIncus();
        deleting.RequestDelete();
        LoadBalancer resting = OnIncus();

        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(deleting.Id, "Deleted"));
        await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(resting.Id, "Deleted"));

        Assert.Equal("Deleted/Deleted/-", Shape(deleting));
        Assert.Equal("NotFound/Exists/-", Shape(resting));
        Assert.Equal(["load_balancer.deleted"], Outcomes());
    }

    // ---------- the requests: repeats, the replaced update, and the lock ----------

    [Fact]
    public async Task Delete_WhileItsDeleteWaits_IsANoOp_WithTheSameRequestStillWaiting()
    {
        LoadBalancer lb = OnIncus();
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
        Guid first = Waiting(lb);

        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));

        Assert.Equal(first, Waiting(lb));
        _commands.Verify(c => c.EnqueueLoadBalancerDeleteAsync(lb.Id, first, "net-ext", lb.Vip, It.IsAny<string>(),
            "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["load_balancer.delete_requested"], _activity.Events.Select(e => e.Action));
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // A delete that replaced a waiting update gave up: desired is what Incus took again, so the replaced spec asked once
    // more is a new update with its own request and row, not a repeat (titan-ops architecture/resource-lifecycle.md#kinds).
    [Fact]
    public async Task AfterADeleteThatReplacedAnUpdateFails_ThePutOfTheReplacedSpec_IsANewUpdate()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        List<LoadBalancerBackendInput> backends = [new(a.Id)];
        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http, Dns], backends));
        Guid replaced = Waiting(lb);
        await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
        await End(lb, Waiting(lb), "Failed");
        _commands.Invocations.Clear();

        await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Http, Dns], backends));

        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
        Assert.NotEqual(replaced, Waiting(lb));
        _commands.Verify(c => c.EnqueueLoadBalancerUpdateAsync(lb, Waiting(lb), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Locked, so a report and a request on the same load balancer take turns
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Theory]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("EndLoadBalancerRequest")]
    [InlineData("ObserveLoadBalancer")]
    [InlineData("UpdateLoadBalancerStatus")]
    public async Task EveryHandler_LoadsTheLoadBalancerLocked(string handler)
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);

        switch (handler)
        {
            case "Update":
                await UpdateHandler().HandleAsync(new UpdateLoadBalancerCommand(lb.Id, [Dns], [new(a.Id)]));
                break;
            case "Delete":
                await DeleteHandler().HandleAsync(new DeleteLoadBalancerCommand(lb.Id));
                break;
            case "EndLoadBalancerRequest":
                await EndHandler().HandleAsync(new EndLoadBalancerRequestCommand(lb.Id, Guid.NewGuid(), "Done"));
                break;
            case "ObserveLoadBalancer":
                await ObserveHandler().HandleAsync(new ObserveLoadBalancerCommand(lb.Id, true));
                break;
            default:
                await OldHandler().HandleAsync(new UpdateLoadBalancerStatusCommand(lb.Id, "Observed", Exists: true));
                break;
        }

        _repo.Verify(r => r.FindByIdForUpdateAsync(lb.Id, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
