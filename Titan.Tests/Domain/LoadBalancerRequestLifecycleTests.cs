using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a load balancer: one waiting operation, ended only by the
// report carrying its id and naming the status it waits in; an observation writes only what it saw and ends nothing; each
// operation's Succeeded and Failed have their effect (titan-ops architecture/resource-lifecycle.md#kinds). Its listener and
// backend rows carry no operation of their own: an operation's end applies them or asks again for what Incus took.
// LoadBalancerStateMachineTests keeps what an operation may do from each situation; this file is about how it ends.
public class LoadBalancerRequestLifecycleTests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private const string Reason = "The load balancer request could not be completed.";
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
        lb.CreateSucceeded(lb.Operation.PendingId!.Value, lb.Vip);
        return lb;
    }

    private static Guid Waiting(LoadBalancer lb) => lb.Operation.PendingId!.Value;

    private static string[] Ports(IEnumerable<LoadBalancerListener> listeners) =>
        listeners.Select(l => $"{l.Protocol} {l.Port}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ---------- the create ----------

    [Fact]
    public void ANewLoadBalancer_WaitsOnItsCreate_WithAnIdAndATime()
    {
        LoadBalancer lb = Creating();

        Assert.Equal(LoadBalancerStatus.Creating, lb.Status);
        Assert.NotEqual(Guid.Empty, lb.Operation.PendingId);
        Assert.True(lb.Operation.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Null(lb.Operation.LastSeenStatus);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheVip_AppliesTheRows_AndEndsIt_Once()
    {
        LoadBalancer lb = Creating();
        Guid create = Waiting(lb);

        bool ended = lb.CreateSucceeded(create, lb.Vip);
        bool again = lb.CreateSucceeded(create, "another");

        Assert.True(ended);
        Assert.False(again);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Null(lb.Operation.PendingId);
        Assert.Equal(["TCP 80"], Ports(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
        Assert.Null(lb.Operation.FailureReason);
    }

    [Fact]
    public void Done_OfTheCreate_WithoutTheVip_IsRefused_BeforeAnythingIsWritten()
    {
        LoadBalancer lb = Creating();

        var refused = Assert.Throws<BusinessException>(() => lb.CreateSucceeded(Waiting(lb), null));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(LoadBalancerStatus.Creating, lb.Status);
        Assert.NotNull(lb.Operation.PendingId);
        Assert.Null(lb.ExternalId);
        Assert.Empty(lb.AppliedListeners);
    }

    [Fact]
    public void Failed_OfTheCreate_RecordsWhy_AndLeavesNothingOnIncus()
    {
        LoadBalancer lb = Creating();

        bool ended = lb.CreateFailed(Waiting(lb), null, Reason, "raw");

        Assert.True(ended);
        Assert.Equal(LoadBalancerStatus.Failed, lb.Status);
        Assert.Null(lb.ExternalId);
        Assert.Equal(Reason, lb.Operation.FailureReason);
        Assert.Equal("raw", lb.Operation.FailureDetail);
        Assert.NotNull(lb.Operation.FailedAt);
        Assert.Null(lb.Operation.PendingId);
        Assert.Empty(lb.AppliedListeners);
        // Never reached Incus: it is deleted here, not through Incus.
        Assert.Null(lb.Delete());
        Assert.True(lb.IsDeleted);
    }

    // The give-up found the VIP on Incus: it is kept, so the delete goes through Incus. The create's one write carries
    // the whole spec, so a VIP on Incus holds what was asked: the rows are applied, and desired and applied never differ
    // with nothing waiting.
    [Fact]
    public void Failed_OfTheCreate_CarryingTheVip_KeepsItAndAvailable_AndAppliesTheRows()
    {
        LoadBalancer lb = Creating();

        lb.CreateFailed(Waiting(lb), lb.Vip, Reason, "raw");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(lb.Vip, lb.ExternalId);
        Assert.NotNull(lb.Operation.FailureReason);
        Assert.Equal(["TCP 80"], Ports(lb.AppliedListeners));
        Assert.Single(lb.AppliedBackends);
        Assert.Equal(lb.Vip, lb.Delete()!.ProviderId);
    }

    // ---------- the update ----------

    [Fact]
    public void Update_Asks_RememberingThatIncusHoldsTheLoadBalancer()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);

        OperationTicket update = lb.Update([Dns], [a]);

        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
        Assert.Equal(update.OperationId, lb.Operation.PendingId);
        Assert.Equal(lb.Vip, update.ProviderId);
        Assert.Equal(LoadBalancerStatus.Available, lb.Operation.LastSeenStatus);
        Assert.Equal(LoadBalancerStatus.Available, lb.InstanceStatus);
    }

    [Fact]
    public void Done_OfTheUpdate_AppliesTheNewSpec_AndDropsTheOldRows()
    {
        Vm a = VmOnIncus();
        Vm b = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        OperationTicket update = lb.Update([Dns], [b]);

        bool ended = lb.UpdateSucceeded(update.OperationId, null);

        Assert.True(ended);
        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(["UDP 53"], Ports(lb.Listeners));
        Assert.Equal([b.Id], lb.Backends.Select(x => x.VmId));
        Assert.All(lb.Listeners, l => Assert.True(l.IsApplied && l.IsDesired));
        Assert.Null(lb.Operation.LastSeenStatus);
    }

    [Fact]
    public void Failed_OfTheUpdate_AsksAgainForWhatIncusTook_AndRecordsWhy()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        OperationTicket update = lb.Update([Dns], [VmOnIncus()]);

        bool ended = lb.UpdateFailed(update.OperationId, null, Reason, "raw");

        Assert.True(ended);
        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(["TCP 80"], Ports(lb.DesiredListeners));
        Assert.Equal(["TCP 80"], Ports(lb.Listeners));
        Assert.Equal([a.Id], lb.DesiredBackends.Select(x => x.VmId));
        Assert.Equal(Reason, lb.Operation.FailureReason);
        Assert.True(lb.HasDesiredSpec([Http], [a.Id]));
    }

    // Incus was seen without the load balancer while the update waited: the give-up returns to what was seen.
    [Fact]
    public void Failed_OfTheUpdate_AfterTheReconcilerFoundItGone_RestsAsNotFound()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket update = lb.Update([Dns], [VmOnIncus()]);
        lb.MarkNotFound();
        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);

        lb.UpdateFailed(update.OperationId, null, Reason, null);

        Assert.Equal(LoadBalancerStatus.NotFound, lb.Status);
        Assert.Null(lb.InstanceStatus);
    }

    // A Done clears the reason an earlier operation left (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void Done_OfALaterRequest_ClearsTheOldReason()
    {
        Vm a = VmOnIncus();
        LoadBalancer lb = OnIncus(a);
        lb.UpdateFailed(lb.Update([Dns], [a]).OperationId, null, Reason, "raw");

        lb.UpdateSucceeded(lb.Update([Dns], [a]).OperationId, null);

        Assert.Null(lb.Operation.FailureReason);
        Assert.Null(lb.Operation.FailureDetail);
        Assert.Null(lb.Operation.FailedAt);
    }

    // ---------- the delete ----------

    [Fact]
    public void Delete_Asks_RememberingWhetherIncusHeldTheLoadBalancer()
    {
        LoadBalancer held = OnIncus();
        LoadBalancer gone = OnIncus();
        gone.MarkNotFound();

        held.Delete();
        gone.Delete();

        Assert.Equal(LoadBalancerStatus.Deleting, held.Status);
        Assert.Equal(LoadBalancerStatus.Available, held.Operation.LastSeenStatus);
        Assert.Equal(LoadBalancerStatus.Deleting, gone.Status);
        Assert.Null(gone.Operation.LastSeenStatus);
    }

    [Fact]
    public void Done_OfTheDelete_EndsTheLoadBalancer()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket delete = lb.Delete()!;
        Assert.Equal(lb.Vip, delete.ProviderId);

        bool ended = lb.DeleteSucceeded(delete.OperationId, null);

        Assert.True(ended);
        Assert.Equal(LoadBalancerStatus.Deleted, lb.Status);
        Assert.True(lb.IsDeleted);
        Assert.False(lb.DeleteSucceeded(delete.OperationId, null));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheLoadBalancerBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket first = lb.Delete()!;

        bool ended = lb.DeleteFailed(first.OperationId, null, Reason, "raw");

        Assert.True(ended);
        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(Reason, lb.Operation.FailureReason);
        OperationTicket second = lb.Delete()!;
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    [Fact]
    public void Failed_OfTheDelete_OfALoadBalancerIncusDidNotHold_RestsAsNotFound()
    {
        LoadBalancer lb = OnIncus();
        lb.MarkNotFound();
        OperationTicket delete = lb.Delete()!;

        lb.DeleteFailed(delete.OperationId, null, Reason, null);

        Assert.Equal(LoadBalancerStatus.NotFound, lb.Status);
        Assert.False(lb.OccupiesQuota);
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

        lb.DeleteFailed(delete.OperationId, null, Reason, "raw");

        Assert.Equal(LoadBalancerStatus.Available, lb.Status);
        Assert.Equal(["TCP 80"], Ports(lb.DesiredListeners));
        Assert.Equal(["TCP 80"], Ports(lb.Listeners));
        Assert.False(lb.HasDesiredSpec([Dns], [a.Id]));
        OperationTicket again = lb.Update([Dns], [a]);
        Assert.NotEqual(update.OperationId, again.OperationId);
        Assert.Equal(LoadBalancerStatus.Updating, lb.Status);
    }

    // ---------- only the matching id, in the matching status, ends an operation ----------

    [Fact]
    public void AReportNamingAnotherRequest_EndsNothing_AndChangesNothing()
    {
        LoadBalancer creating = Creating();
        LoadBalancer updating = OnIncus();
        updating.Update([Dns], [VmOnIncus()]);

        Assert.False(creating.CreateSucceeded(Guid.NewGuid(), creating.Vip));
        Assert.False(creating.CreateFailed(Guid.NewGuid(), creating.Vip, Reason, "raw"));
        Assert.False(updating.UpdateFailed(Guid.NewGuid(), null, Reason, "raw"));
        Assert.False(updating.UpdateSucceeded(Guid.NewGuid(), null));
        // Another load balancer's operation id is not this one's.
        Assert.False(creating.CreateSucceeded(Waiting(updating), creating.Vip));
        // The right id, the wrong operation.
        Assert.False(updating.DeleteSucceeded(Waiting(updating), null));
        Assert.False(updating.DeleteFailed(Waiting(updating), null, Reason, "raw"));
        Assert.False(creating.UpdateSucceeded(Waiting(creating), null));

        Assert.Equal(LoadBalancerStatus.Creating, creating.Status);
        Assert.Null(creating.ExternalId);
        Assert.Equal(LoadBalancerStatus.Updating, updating.Status);
        Assert.Equal(["UDP 53"], Ports(updating.DesiredListeners));
        Assert.Null(updating.Operation.FailureReason);
    }

    // The delete replaced the update: the update's report names an operation no longer waiting.
    [Fact]
    public void TheReportOfAnUpdateADeleteReplaced_EndsNothing()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket update = lb.Update([Dns], [VmOnIncus()]);
        OperationTicket delete = lb.Delete()!;

        Assert.False(lb.UpdateSucceeded(update.OperationId, null));
        Assert.False(lb.UpdateFailed(update.OperationId, null, Reason, "raw"));

        Assert.Equal(LoadBalancerStatus.Deleting, lb.Status);
        Assert.Equal(delete.OperationId, lb.Operation.PendingId);
        Assert.Null(lb.Operation.FailureReason);
    }

    // ---------- observations end nothing ----------

    [Fact]
    public void Observations_WhileAnOperationWaits_RecordWhatWasSeen_AndEndNothing()
    {
        LoadBalancer creating = Creating();
        LoadBalancer updating = OnIncus();
        updating.Update([Dns], [VmOnIncus()]);
        LoadBalancer deleting = OnIncus();
        deleting.Delete();

        creating.MarkNotFound();
        updating.MarkObserved();
        deleting.MarkNotFound();

        Assert.Equal(LoadBalancerStatus.Creating, creating.Status);
        Assert.Null(creating.Operation.LastSeenStatus);
        Assert.Equal(LoadBalancerStatus.Updating, updating.Status);
        Assert.Equal(LoadBalancerStatus.Available, updating.Operation.LastSeenStatus);
        Assert.Equal(LoadBalancerStatus.Deleting, deleting.Status);
        Assert.Null(deleting.Operation.LastSeenStatus);
        Assert.Equal(["TCP 80"], Ports(updating.AppliedListeners));
    }

    [Fact]
    public void ADeletedLoadBalancer_AbsorbsEveryReportAndObservation()
    {
        LoadBalancer lb = OnIncus();
        OperationTicket delete = lb.Delete()!;
        lb.DeleteSucceeded(delete.OperationId, null);

        lb.MarkObserved();
        lb.MarkNotFound();
        Assert.False(lb.DeleteFailed(delete.OperationId, null, Reason, "raw"));
        Assert.False(lb.DeleteSucceeded(delete.OperationId, null));
        Assert.False(lb.CreateSucceeded(delete.OperationId, lb.Vip));

        Assert.Equal(LoadBalancerStatus.Deleted, lb.Status);
        Assert.Equal("LOAD_BALANCER_INVALID_STATE", Assert.Throws<BusinessException>(() => lb.Delete()).Code);
    }

    [Fact]
    public void AFailedLoadBalancer_IgnoresObservations()
    {
        LoadBalancer lb = Creating();
        lb.CreateFailed(Waiting(lb), null, Reason, null);

        lb.MarkObserved();
        lb.MarkNotFound();

        Assert.Equal(LoadBalancerStatus.Failed, lb.Status);
        Assert.False(lb.OccupiesQuota);
    }

    [Fact]
    public void NothingWaits_OnceTheOperationEnded()
    {
        LoadBalancer lb = OnIncus();

        Assert.Null(lb.Operation.PendingId);
        Assert.Null(lb.Operation.AskedAt);
        Assert.Equal(LoadBalancerStatus.Available, lb.InstanceStatus);
    }
}
