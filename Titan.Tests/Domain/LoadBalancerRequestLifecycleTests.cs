using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a load balancer: one waiting request, ended only by the
// report carrying its id; an observation writes current and ends nothing; each kind's Done and Failed have their
// effect (titan-ops architecture/resource-lifecycle.md#kinds). Its listener and backend rows carry no request of their
// own: a request's end applies them or asks again for what Incus took. LoadBalancerStateMachineTests keeps what a
// request may do from each situation; this file is about how a request ends.
public class LoadBalancerRequestLifecycleTests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private static readonly Guid NetworkId = Guid.NewGuid();
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;
    private static readonly LoadBalancerListenerSpec Http = new(LoadBalancerProtocol.TCP, 80, 8080);
    private static readonly LoadBalancerListenerSpec Dns = new(LoadBalancerProtocol.UDP, 53, 53);

    private static Vm VmOnIncus()
    {
        Vm vm = Vm.Create(Tenant, NetworkId, $"vm-{Guid.NewGuid():N}"[..12], "m", "tpl", "cluster-a", 20);
        vm.EndRequest(vm.Pending!.Id,
            VmRequestEnd.Done($"vm{Guid.NewGuid():N}"[..28], new VmObservation(VmState.Running, "10.0.0.5", null)));
        return vm;
    }

    private static LoadBalancer Creating(params Vm[] backends) =>
        LoadBalancer.Create(Tenant, "web", NetworkId, "net-ext", Subnet, "cluster-a", LoadBalancerVip.For(Subnet, 1), [Http],
            backends.Length == 0 ? [VmOnIncus()] : backends);

    private static LoadBalancer OnIncus(params Vm[] backends)
    {
        LoadBalancer lb = Creating(backends);
        lb.EndRequest(lb.Pending!.Id, LoadBalancerRequestEnd.Done(lb.Vip));
        return lb;
    }

    private static LoadBalancerRequestEnd Failed(string? externalId = null) =>
        LoadBalancerRequestEnd.Failed(externalId, "The load balancer request could not be completed.", "raw");

    private static string Shape(LoadBalancer lb) =>
        $"{lb.CurrentExistence}/{lb.DesiredExistence}/{(lb.Transition == null ? "-" : lb.Transition.Value.ToString())}";

    private static string[] Ports(IEnumerable<LoadBalancerListener> listeners) =>
        listeners.Select(l => $"{l.Protocol} {l.Port}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ---------- the create ----------

    [Fact]
    public void ANewLoadBalancer_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        LoadBalancer lb = Creating();

        Assert.Equal(LoadBalancerRequestKind.Create, lb.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, lb.Pending.Id);
        Assert.True(lb.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(LoadBalancerTransition.Creating, lb.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheVipAndExistence_AppliesTheRows_AndEndsIt_Once()
    {
        LoadBalancer lb = Creating();
        Guid create = lb.Pending!.Id;

        PendingRequest<LoadBalancerRequestKind>? ended = lb.EndRequest(create, LoadBalancerRequestEnd.Done(lb.Vip));
        PendingRequest<LoadBalancerRequestKind>? again = lb.EndRequest(create, LoadBalancerRequestEnd.Done("another"));

        Assert.Equal((LoadBalancerRequestKind.Create, create), (ended!.Kind, ended.Id));
        Assert.Null(again);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["TCP 80"], Ports(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
        Assert.Null(lb.RequestFailureReason);
    }

    [Fact]
    public void Failed_OfTheCreate_RecordsWhy_AndLeavesNothingOnIncus()
    {
        LoadBalancer lb = Creating();

        lb.EndRequest(lb.Pending!.Id, Failed());

        Assert.Equal("NotCreated/Exists/-", Shape(lb));
        Assert.Null(lb.ExternalId);
        Assert.Equal("The load balancer request could not be completed.", lb.RequestFailureReason);
        Assert.Equal("raw", lb.RequestFailureDetail);
        Assert.NotNull(lb.RequestFailedAt);
        Assert.Empty(lb.AppliedListeners);
        // Never reached Incus: it is deleted here, not through Incus.
        Assert.Null(lb.RequestDelete());
        Assert.True(lb.IsTerminal);
    }

    // The give-up found the VIP on Incus: it is kept, so the delete goes through Incus. The create's one write carries
    // the whole spec, so a VIP on Incus holds what was asked: the rows are applied, and desired and applied never differ
    // with nothing waiting.
    [Fact]
    public void Failed_OfTheCreate_CarryingTheVip_KeepsItAndExistence_AndAppliesTheRows()
    {
        LoadBalancer lb = Creating();

        lb.EndRequest(lb.Pending!.Id, Failed(lb.Vip));

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.NotNull(lb.RequestFailureReason);
        Assert.Equal(["TCP 80"], Ports(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
        Assert.Equal(lb.Vip, lb.RequestDelete()!.ProviderId);
    }

    // ---------- the update ----------

    [Fact]
    public void Done_OfTheUpdate_AppliesTheNewSpec_AndDropsTheOldRows()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        RequestTicket update = lb.RequestUpdate([Dns], [b]);
        Assert.Equal((LoadBalancerRequestKind.Update, update.RequestId), (lb.Pending!.Kind, lb.Pending.Id));
        Assert.Equal(lb.Vip, update.ProviderId);

        PendingRequest<LoadBalancerRequestKind>? ended = lb.EndRequest(update.RequestId, LoadBalancerRequestEnd.Done(null));

        Assert.Equal(LoadBalancerRequestKind.Update, ended!.Kind);
        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["UDP 53"], Ports(lb.Listeners));
        Assert.Equal([b.Id], lb.Backends.Select(x => x.VmId));
        Assert.All(lb.Listeners, l => Assert.True(l.IsApplied && l.IsDesired));
    }

    [Fact]
    public void Failed_OfTheUpdate_AsksAgainForWhatIncusTook_AndRecordsWhy()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        RequestTicket update = lb.RequestUpdate([Dns], [VmOnIncus()]);

        lb.EndRequest(update.RequestId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["TCP 80"], Ports(lb.DesiredListeners));
        Assert.Equal(["TCP 80"], Ports(lb.Listeners));
        Assert.Equal([a.Id], lb.DesiredBackends.Select(x => x.VmId));
        Assert.Equal("The load balancer request could not be completed.", lb.RequestFailureReason);
        Assert.True(lb.HasDesiredSpec([Http], [a.Id]));
    }

    // A Done clears the reason an earlier request left (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void Done_OfALaterRequest_ClearsTheOldReason()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        lb.EndRequest(lb.RequestUpdate([Dns], [a]).RequestId, Failed());

        lb.EndRequest(lb.RequestUpdate([Dns], [a]).RequestId, LoadBalancerRequestEnd.Done(null));

        Assert.Null(lb.RequestFailureReason);
        Assert.Null(lb.RequestFailureDetail);
        Assert.Null(lb.RequestFailedAt);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheLoadBalancer()
    {
        LoadBalancer lb = OnIncus();
        RequestTicket delete = lb.RequestDelete()!;
        Assert.Equal(lb.Vip, delete.ProviderId);

        PendingRequest<LoadBalancerRequestKind>? ended = lb.EndRequest(delete.RequestId, LoadBalancerRequestEnd.Done(null));

        Assert.Equal(LoadBalancerRequestKind.Delete, ended!.Kind);
        Assert.Equal("Deleted/Deleted/-", Shape(lb));
        Assert.True(lb.IsTerminal);
        Assert.Null(lb.EndRequest(delete.RequestId, LoadBalancerRequestEnd.Done(null)));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheLoadBalancerBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        LoadBalancer lb = OnIncus();
        RequestTicket first = lb.RequestDelete()!;

        lb.EndRequest(first.RequestId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal("The load balancer request could not be completed.", lb.RequestFailureReason);
        RequestTicket second = lb.RequestDelete()!;
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    // A delete that replaced a waiting update gives back what Incus took, not the replaced update's spec: desired and
    // applied agree again with nothing waiting, so the replaced spec asked again is a new update.
    [Fact]
    public void Failed_OfADeleteThatReplacedAnUpdate_AsksAgainForWhatIncusTook_SoTheReplacedSpecIsANewUpdate()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        RequestTicket update = lb.RequestUpdate([Dns], [a]);
        RequestTicket delete = lb.RequestDelete()!;

        lb.EndRequest(delete.RequestId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["TCP 80"], Ports(lb.DesiredListeners));
        Assert.Equal(["TCP 80"], Ports(lb.Listeners));
        Assert.False(lb.HasDesiredSpec([Dns], [a.Id]));
        RequestTicket again = lb.RequestUpdate([Dns], [a]);
        Assert.NotEqual(update.RequestId, again.RequestId);
        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
    }

    // ---------- only the matching id ends a request ----------

    [Fact]
    public void AReportNamingAnotherRequest_EndsNothing_AndChangesNothing()
    {
        LoadBalancer creating = Creating();
        LoadBalancer updating = OnIncus();
        updating.RequestUpdate([Dns], [VmOnIncus()]);

        Assert.Null(creating.EndRequest(Guid.NewGuid(), LoadBalancerRequestEnd.Done(creating.Vip)));
        Assert.Null(updating.EndRequest(Guid.NewGuid(), Failed()));
        // Another load balancer's request id is not this one's.
        Assert.Null(creating.EndRequest(updating.Pending!.Id, LoadBalancerRequestEnd.Done(creating.Vip)));

        Assert.Equal("NotCreated/Exists/Creating", Shape(creating));
        Assert.Null(creating.ExternalId);
        Assert.Equal("Exists/Exists/Updating", Shape(updating));
        Assert.Equal(["UDP 53"], Ports(updating.DesiredListeners));
        Assert.Null(updating.RequestFailureReason);
    }

    // The delete replaced the update: the update's report names a request no longer waiting.
    [Fact]
    public void TheReportOfAnUpdateADeleteReplaced_EndsNothing()
    {
        LoadBalancer lb = OnIncus();
        RequestTicket update = lb.RequestUpdate([Dns], [VmOnIncus()]);
        lb.RequestDelete();

        Assert.Null(lb.EndRequest(update.RequestId, LoadBalancerRequestEnd.Done(null)));
        Assert.Null(lb.EndRequest(update.RequestId, Failed()));

        Assert.Equal("Exists/Deleted/Deleting", Shape(lb));
        Assert.Null(lb.RequestFailureReason);
    }

    // ---------- observations end nothing ----------

    [Fact]
    public void Observations_WriteExistence_AndEndNoRequest()
    {
        LoadBalancer creating = Creating();
        LoadBalancer updating = OnIncus();
        updating.RequestUpdate([Dns], [VmOnIncus()]);
        LoadBalancer deleting = OnIncus();
        deleting.RequestDelete();

        creating.MarkNotFound();
        updating.MarkObserved();
        deleting.MarkNotFound();

        Assert.Equal("NotFound/Exists/Creating", Shape(creating));
        Assert.Equal("Exists/Exists/Updating", Shape(updating));
        Assert.Equal("NotFound/Deleted/Deleting", Shape(deleting));
        Assert.Equal(["TCP 80"], Ports(updating.AppliedListeners));
    }

    [Fact]
    public void ADeletedLoadBalancer_AbsorbsEveryReportAndObservation()
    {
        LoadBalancer lb = OnIncus();
        RequestTicket delete = lb.RequestDelete()!;
        lb.EndRequest(delete.RequestId, LoadBalancerRequestEnd.Done(null));

        lb.MarkObserved();
        lb.MarkNotFound();
        Assert.Null(lb.EndRequest(delete.RequestId, Failed()));

        Assert.Equal("Deleted/Deleted/-", Shape(lb));
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => lb.RequestDelete()).Code);
    }

    [Fact]
    public void Pending_IsNull_OnceNothingWaits()
    {
        LoadBalancer lb = OnIncus();

        Assert.Null(lb.Pending);
        Assert.Null(lb.Transition);
    }
}
