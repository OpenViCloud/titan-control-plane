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
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, $"vm{Guid.NewGuid():N}"[..28], VmStatus.Running, "10.0.0.5", null);
        return vm;
    }

    private static LoadBalancer Creating(params Vm[] backends) =>
        LoadBalancer.Create(Tenant, "web", NetworkId, "net-ext", Subnet, "cluster-a", LoadBalancerVip.For(Subnet, 1), [Http],
            backends.Length == 0 ? [VmOnIncus()] : backends);

    private static LoadBalancer OnIncus(params Vm[] backends)
    {
        LoadBalancer lb = Creating(backends);
        lb.FinishOperation(lb.Pending!.Id, lb.Vip);
        return lb;
    }

    private static LoadBalancerOperationKind? Failed(LoadBalancer lb, Guid operationId,
        string? externalId = null) =>
        lb.FailOperation(operationId, externalId, "The load balancer request could not be completed.", "raw");

    private static string Shape(LoadBalancer lb) =>
        $"{lb.CurrentExistence}/{lb.DesiredExistence}/{(lb.Transition == null ? "-" : lb.Transition.Value.ToString())}";

    private static string[] Ports(IEnumerable<LoadBalancerListener> listeners) =>
        listeners.Select(l => $"{l.Protocol} {l.Port}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ---------- the create ----------

    [Fact]
    public void ANewLoadBalancer_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        LoadBalancer lb = Creating();

        Assert.Equal(LoadBalancerOperationKind.Create, lb.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, lb.Pending.Id);
        Assert.True(lb.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(LoadBalancerTransition.Creating, lb.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheVipAndExistence_AppliesTheRows_AndEndsIt_Once()
    {
        LoadBalancer lb = Creating();
        Guid create = lb.Pending!.Id;

        LoadBalancerOperationKind? ended = lb.FinishOperation(create, lb.Vip);
        LoadBalancerOperationKind? again = lb.FinishOperation(create, "another");

        Assert.Equal(LoadBalancerOperationKind.Create, ended);
        Assert.Null(again);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["TCP 80"], Ports(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
        Assert.Null(lb.OperationFailureReason);
    }

    [Fact]
    public void Failed_OfTheCreate_RecordsWhy_AndLeavesNothingOnIncus()
    {
        LoadBalancer lb = Creating();

        Failed(lb, lb.Pending!.Id);

        Assert.Equal("NotCreated/Exists/-", Shape(lb));
        Assert.Null(lb.ExternalId);
        Assert.Equal("The load balancer request could not be completed.", lb.OperationFailureReason);
        Assert.Equal("raw", lb.OperationFailureDetail);
        Assert.NotNull(lb.OperationFailedAt);
        Assert.Empty(lb.AppliedListeners);
        // Never reached Incus: it is deleted here, not through Incus.
        Assert.Null(lb.Delete());
        Assert.True(lb.IsDeleted);
    }

    // The give-up found the VIP on Incus: it is kept, so the delete goes through Incus. The create's one write carries
    // the whole spec, so a VIP on Incus holds what was asked: the rows are applied, and desired and applied never differ
    // with nothing waiting.
    [Fact]
    public void Failed_OfTheCreate_CarryingTheVip_KeepsItAndExistence_AndAppliesTheRows()
    {
        LoadBalancer lb = Creating();

        Failed(lb, lb.Pending!.Id, lb.Vip);

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.NotNull(lb.OperationFailureReason);
        Assert.Equal(["TCP 80"], Ports(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
        Assert.Equal(lb.Vip, lb.Delete()!.ProviderId);
    }

    // ---------- the update ----------

    [Fact]
    public void Done_OfTheUpdate_AppliesTheNewSpec_AndDropsTheOldRows()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        OperationTicket update = lb.Update([Dns], [b]);
        Assert.Equal((LoadBalancerOperationKind.Update, update.OperationId), (lb.Pending!.Kind, lb.Pending.Id));
        Assert.Equal(lb.Vip, update.ProviderId);

        LoadBalancerOperationKind? ended = lb.FinishOperation(update.OperationId, null);

        Assert.Equal(LoadBalancerOperationKind.Update, ended);
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
        OperationTicket update = lb.Update([Dns], [VmOnIncus()]);

        Failed(lb, update.OperationId);

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["TCP 80"], Ports(lb.DesiredListeners));
        Assert.Equal(["TCP 80"], Ports(lb.Listeners));
        Assert.Equal([a.Id], lb.DesiredBackends.Select(x => x.VmId));
        Assert.Equal("The load balancer request could not be completed.", lb.OperationFailureReason);
        Assert.True(lb.HasDesiredSpec([Http], [a.Id]));
    }

    // A Done clears the reason an earlier request left (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void Done_OfALaterRequest_ClearsTheOldReason()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        Failed(lb, lb.Update([Dns], [a]).OperationId);

        lb.FinishOperation(lb.Update([Dns], [a]).OperationId, null);

        Assert.Null(lb.OperationFailureReason);
        Assert.Null(lb.OperationFailureDetail);
        Assert.Null(lb.OperationFailedAt);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheLoadBalancer()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket delete = lb.Delete()!;
        Assert.Equal(lb.Vip, delete.ProviderId);

        LoadBalancerOperationKind? ended = lb.FinishOperation(delete.OperationId, null);

        Assert.Equal(LoadBalancerOperationKind.Delete, ended);
        Assert.Equal("Deleted/Deleted/-", Shape(lb));
        Assert.True(lb.IsDeleted);
        Assert.Null(lb.FinishOperation(delete.OperationId, null));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheLoadBalancerBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket first = lb.Delete()!;

        Failed(lb, first.OperationId);

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal("The load balancer request could not be completed.", lb.OperationFailureReason);
        OperationTicket second = lb.Delete()!;
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    // A delete that replaced a waiting update gives back what Incus took, not the replaced update's spec: desired and
    // applied agree again with nothing waiting, so the replaced spec asked again is a new update.
    [Fact]
    public void Failed_OfADeleteThatReplacedAnUpdate_AsksAgainForWhatIncusTook_SoTheReplacedSpecIsANewUpdate()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        OperationTicket update = lb.Update([Dns], [a]);
        OperationTicket delete = lb.Delete()!;

        Failed(lb, delete.OperationId);

        Assert.Equal("Exists/Exists/-", Shape(lb));
        Assert.Equal(["TCP 80"], Ports(lb.DesiredListeners));
        Assert.Equal(["TCP 80"], Ports(lb.Listeners));
        Assert.False(lb.HasDesiredSpec([Dns], [a.Id]));
        OperationTicket again = lb.Update([Dns], [a]);
        Assert.NotEqual(update.OperationId, again.OperationId);
        Assert.Equal(LoadBalancerTransition.Updating, lb.Transition);
    }

    // ---------- only the matching id ends a request ----------

    [Fact]
    public void AReportNamingAnotherRequest_EndsNothing_AndChangesNothing()
    {
        LoadBalancer creating = Creating();
        LoadBalancer updating = OnIncus();
        updating.Update([Dns], [VmOnIncus()]);

        Assert.Null(creating.FinishOperation(Guid.NewGuid(), creating.Vip));
        Assert.Null(Failed(updating, Guid.NewGuid()));
        // Another load balancer's request id is not this one's.
        Assert.Null(creating.FinishOperation(updating.Pending!.Id, creating.Vip));

        Assert.Equal("NotCreated/Exists/Creating", Shape(creating));
        Assert.Null(creating.ExternalId);
        Assert.Equal("Exists/Exists/Updating", Shape(updating));
        Assert.Equal(["UDP 53"], Ports(updating.DesiredListeners));
        Assert.Null(updating.OperationFailureReason);
    }

    // The delete replaced the update: the update's report names a request no longer waiting.
    [Fact]
    public void TheReportOfAnUpdateADeleteReplaced_EndsNothing()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket update = lb.Update([Dns], [VmOnIncus()]);
        lb.Delete();

        Assert.Null(lb.FinishOperation(update.OperationId, null));
        Assert.Null(Failed(lb, update.OperationId));

        Assert.Equal("Exists/Deleted/Deleting", Shape(lb));
        Assert.Null(lb.OperationFailureReason);
    }

    // ---------- observations end nothing ----------

    [Fact]
    public void Observations_WriteExistence_AndEndNoRequest()
    {
        LoadBalancer creating = Creating();
        LoadBalancer updating = OnIncus();
        updating.Update([Dns], [VmOnIncus()]);
        LoadBalancer deleting = OnIncus();
        deleting.Delete();

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
        OperationTicket delete = lb.Delete()!;
        lb.FinishOperation(delete.OperationId, null);

        lb.MarkObserved();
        lb.MarkNotFound();
        Assert.Null(Failed(lb, delete.OperationId));

        Assert.Equal("Deleted/Deleted/-", Shape(lb));
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => lb.Delete()).Code);
    }

    [Fact]
    public void Pending_IsNull_OnceNothingWaits()
    {
        LoadBalancer lb = OnIncus();

        Assert.Null(lb.Pending);
        Assert.Null(lb.Transition);
    }
}
