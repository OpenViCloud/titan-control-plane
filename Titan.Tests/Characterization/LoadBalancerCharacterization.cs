using Titan.Application.Services.Compute.LoadBalancer;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Tests.Fakes;
using Xunit.Abstractions;
using static Titan.Tests.Characterization.MoveSequences;

namespace Titan.Tests.Characterization;

// A load balancer root with two listener specs and two backends. OldSide calls the frozen copy in Old/, CurrentSide the
// live aggregate; the live side's moves, PendingId and ViewOf are what the next change to LoadBalancer re-points. The
// initial state is a created load balancer waiting on Incus; the moves reach every other state.
[Trait("Category", "Characterization")]
public class LoadBalancerCharacterization
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private static readonly int MaxLength = CharacterizationDepth.For(4);

    private static readonly Tenant Owner = TestTenant.WithRoomToSpare();
    private static readonly Guid NetworkId = Guid.NewGuid();
    private static readonly LoadBalancerVip Vip = LoadBalancerVip.For(Subnet, 1);
    private static readonly string VipAddress = Vip.Address;

    private static readonly LoadBalancerListenerSpec Http = new(LoadBalancerProtocol.TCP, 80, 8080);
    private static readonly LoadBalancerListenerSpec Dns = new(LoadBalancerProtocol.UDP, 53, 53);
    private static readonly Vm First = VmOnIncus();
    private static readonly Vm Second = VmOnIncus();

    // The spec the load balancer is created with, and one that differs from it in listeners and backends.
    private static readonly LoadBalancerListenerSpec[] ListenersA = [Http];
    private static readonly Vm[] BackendsA = [First];
    private static readonly LoadBalancerListenerSpec[] ListenersB = [Dns];
    private static readonly Vm[] BackendsB = [First, Second];

    private readonly ITestOutputHelper _output;

    public LoadBalancerCharacterization(ITestOutputHelper output) => _output = output;

    private static Vm VmOnIncus()
    {
        var vm = Vm.Create(Owner.Id, NetworkId, $"vm-{Guid.NewGuid():N}", "m", "tpl", "cluster-a", 20);
        vm.MarkRunning($"vm{Guid.NewGuid():N}"[..28], "10.0.0.5");
        return vm;
    }

    /// What a caller can read about the load balancer after a move. Status is the one status and Seen whether Incus was
    /// last seen holding it while an operation waits: the live aggregate keeps them, the frozen one is mapped to them by
    /// the rule the one-status migration applies to stored rows.
    internal sealed record LoadBalancerView(
        bool OccupiesQuota, bool IsDeleted, bool IsDeletedOrDeleting, bool HasSpecA, bool HasSpecB,
        string Status, string Seen, bool HasPending, bool HasFailureReason,
        string ExternalId, string Name, string DesiredListeners, string AppliedListeners,
        string DesiredBackends, string AppliedBackends, int ListenerRows, int BackendRows);

    private static string Listeners(IEnumerable<(LoadBalancerProtocol Protocol, int Port, int TargetPort)> rows) =>
        string.Join(",", rows.Select(r => $"{r.Protocol}:{r.Port}>{r.TargetPort}").OrderBy(x => x, StringComparer.Ordinal));

    private static string Backends(IEnumerable<Guid> vmIds) =>
        string.Join(",", vmIds.Select(id => id == First.Id ? "first" : id == Second.Id ? "second" : "other").OrderBy(x => x, StringComparer.Ordinal));

    private static readonly Guid[] FirstOnly = [First.Id];
    private static readonly Guid[] BothVms = [First.Id, Second.Id];

    internal static Side<Old.LoadBalancer, LoadBalancerView> OldSide() => new(
        () => Old.LoadBalancer.Create(Owner.Id, "web", NetworkId, "net-ext", Subnet, "cluster-a", Vip, ListenersA, BackendsA),
        OldMoves(),
        ViewOf);

    internal static Side<LoadBalancer, LoadBalancerView> CurrentSide() => new(
        () => LoadBalancer.Create(Owner.Id, "web", NetworkId, "net-ext", Subnet, "cluster-a", Vip, ListenersA, BackendsA),
        CurrentMoves(),
        ViewOf);

    // ---- the old aggregate

    private static Guid PendingId(Old.LoadBalancer lb) => lb.Pending?.Id ?? Guid.NewGuid();

    // The old report methods return the kind of the operation they ended, or null; the live ones return a bool. The
    // observer's question is the same: did the report end the operation.
    private static List<Move<Old.LoadBalancer>> OldMoves() =>
    [
        new("UpdateSame", lb => Returning(() => lb.Update(ListenersA, BackendsA)?.ProviderId)),
        new("UpdateOther", lb => Returning(() => lb.Update(ListenersB, BackendsB)?.ProviderId)),
        new("Delete", lb => Returning(() => lb.Delete()?.ProviderId)),
        new("Done", lb => Returning(() => lb.FinishOperation(PendingId(lb), null) != null)),
        new("DoneVip", lb => Returning(() => lb.FinishOperation(PendingId(lb), VipAddress) != null)),
        new("DoneForeign", lb => Returning(() => lb.FinishOperation(Guid.NewGuid(), VipAddress) != null)),
        new("Fail", lb => Returning(() => lb.FailOperation(PendingId(lb), null, "reason", "detail") != null)),
        new("FailVip", lb => Returning(() => lb.FailOperation(PendingId(lb), VipAddress, "reason", "detail") != null)),
        new("FailForeign", lb => Returning(() => lb.FailOperation(Guid.NewGuid(), VipAddress, "reason", "detail") != null)),
        new("Observed", lb => Observing(lb.ExternalId, lb.MarkObserved)),
        new("NotFound", lb => Observing(lb.ExternalId, lb.MarkNotFound)),
        new("Rename", lb => Doing(() => lb.Rename("renamed"))),
    ];

    // The reconciler lists only load balancers that have a VIP (ListReconcilableAsync), so no observation reaches one
    // without: on both sides that move is skipped there, and ObservationsOfALoadBalancerWithoutAVip_AreTheOnlyKnownDifference
    // pins what the two sides do with it.
    private static string Observing(string? externalId, Action observe) =>
        externalId == null ? "skipped" : Doing(observe);

    private static LoadBalancerView ViewOf(Old.LoadBalancer lb) => new(
        OccupiesQuota: lb.OccupiesQuota,
        IsDeleted: lb.IsDeleted,
        IsDeletedOrDeleting: lb.IsDeletedOrDeleting,
        HasSpecA: lb.HasDesiredSpec(ListenersA, FirstOnly),
        HasSpecB: lb.HasDesiredSpec(ListenersB, BothVms),
        Status: MappedStatus(lb),
        Seen: lb.Pending != null && lb.CurrentExistence == Old.CurrentExistence.Exists ? "Available" : "-",
        HasPending: lb.Pending != null,
        HasFailureReason: lb.OperationFailureReason != null,
        ExternalId: lb.ExternalId ?? "-",
        Name: lb.Name,
        DesiredListeners: Listeners(lb.DesiredListeners.Select(l => (l.Protocol, l.Port, l.TargetPort))),
        AppliedListeners: Listeners(lb.AppliedListeners.Select(l => (l.Protocol, l.Port, l.TargetPort))),
        DesiredBackends: Backends(lb.DesiredBackends.Select(b => b.VmId)),
        AppliedBackends: Backends(lb.AppliedBackends.Select(b => b.VmId)),
        ListenerRows: lb.Listeners.Count,
        BackendRows: lb.Backends.Count);

    // The rule the one-status migration applies to a stored row, in its order.
    private static string MappedStatus(Old.LoadBalancer lb)
    {
        if (lb.CurrentExistence == Old.CurrentExistence.Deleted) return "Deleted";
        switch (lb.Pending?.Kind)
        {
            case Old.LoadBalancerOperationKind.Delete:
                return "Deleting";
            case Old.LoadBalancerOperationKind.Update:
                return "Updating";
            case Old.LoadBalancerOperationKind.Create:
                return "Creating";
        }
        if (lb.ExternalId == null) return "Failed";
        if (lb.CurrentExistence == Old.CurrentExistence.NotFound) return "NotFound";
        return "Available";
    }

    // ---- the live aggregate

    private static Guid PendingId(LoadBalancer lb) => lb.Operation.PendingId ?? Guid.NewGuid();

    private static List<Move<LoadBalancer>> CurrentMoves() =>
    [
        new("UpdateSame", lb => Returning(() => lb.Update(ListenersA, BackendsA)?.ProviderId)),
        new("UpdateOther", lb => Returning(() => lb.Update(ListenersB, BackendsB)?.ProviderId)),
        new("Delete", lb => Returning(() => lb.Delete()?.ProviderId)),
        new("Done", lb => Returning(() => Finish(lb, PendingId(lb), null))),
        new("DoneVip", lb => Returning(() => Finish(lb, PendingId(lb), VipAddress))),
        new("DoneForeign", lb => Returning(() => Finish(lb, Guid.NewGuid(), VipAddress))),
        new("Fail", lb => Returning(() => Fail(lb, PendingId(lb), null))),
        new("FailVip", lb => Returning(() => Fail(lb, PendingId(lb), VipAddress))),
        new("FailForeign", lb => Returning(() => Fail(lb, Guid.NewGuid(), VipAddress))),
        new("Observed", lb => Observing(lb.ExternalId, lb.MarkObserved)),
        new("NotFound", lb => Observing(lb.ExternalId, lb.MarkNotFound)),
        new("Rename", lb => Doing(() => lb.Rename("renamed"))),
    ];

    // The report goes through the handlers' own dispatch, which picks the method by the status the load balancer waits in.
    private static bool Finish(LoadBalancer lb, Guid operationId, string? externalId) =>
        LoadBalancerReports.FinishOperation(lb, operationId, externalId, new FakeActivityRecorder()) != null;

    private static bool Fail(LoadBalancer lb, Guid operationId, string? externalId) =>
        LoadBalancerReports.FailOperation(lb, operationId, externalId, "reason", "detail", new FakeActivityRecorder()) != null;

    private static LoadBalancerView ViewOf(LoadBalancer lb) => new(
        OccupiesQuota: lb.OccupiesQuota,
        IsDeleted: lb.IsDeleted,
        IsDeletedOrDeleting: lb.IsDeletedOrDeleting,
        HasSpecA: lb.HasDesiredSpec(ListenersA, FirstOnly),
        HasSpecB: lb.HasDesiredSpec(ListenersB, BothVms),
        Status: lb.Status.ToString(),
        Seen: lb.Operation.LastSeenStatus?.ToString() ?? "-",
        HasPending: lb.Operation.PendingId != null,
        HasFailureReason: lb.Operation.FailureReason != null,
        ExternalId: lb.ExternalId ?? "-",
        Name: lb.Name,
        DesiredListeners: Listeners(lb.DesiredListeners.Select(l => (l.Protocol, l.Port, l.TargetPort))),
        AppliedListeners: Listeners(lb.AppliedListeners.Select(l => (l.Protocol, l.Port, l.TargetPort))),
        DesiredBackends: Backends(lb.DesiredBackends.Select(b => b.VmId)),
        AppliedBackends: Backends(lb.AppliedBackends.Select(b => b.VmId)),
        ListenerRows: lb.Listeners.Count,
        BackendRows: lb.Backends.Count);

    // ---- tests

    [Fact]
    public void LoadBalancer_FrozenCopyAgainstLiveAggregate_NoSequenceDiffers()
    {
        CharacterizationReport report = Compare(OldSide(), CurrentSide(), MaxLength);

        _output.WriteLine($"LoadBalancer: {report.Summary}");
        Assert.Equal(Count(report.AlphabetSize, MaxLength), report.SequencesRun);
        report.AssertNoMismatches();
    }

    // The two rows where the sides differ, and the only ones: they are reached through the observation
    // endpoint but never through the worker, which observes only load balancers that have a VIP. The old model wrote
    // "Exists" or "NotFound" over a row whatever the row was; the one status does not count a create that waits as gone
    // and ignores an observation of a create that gave up.
    [Fact]
    public void ObservationsOfALoadBalancerWithoutAVip_AreTheOnlyKnownDifference()
    {
        Old.LoadBalancer oldWaiting = OldSide().Start();
        LoadBalancer waiting = CurrentSide().Start();
        oldWaiting.MarkNotFound();
        waiting.MarkNotFound();
        Assert.False(oldWaiting.OccupiesQuota);
        Assert.True(waiting.OccupiesQuota);

        Old.LoadBalancer oldFailed = OldSide().Start();
        LoadBalancer failed = CurrentSide().Start();
        oldFailed.FailOperation(oldFailed.Pending!.Id, null, "reason", "detail");
        failed.CreateFailed(failed.Operation.PendingId!.Value, null, "reason", "detail");
        oldFailed.MarkObserved();
        failed.MarkObserved();
        Assert.True(oldFailed.OccupiesQuota);
        Assert.False(failed.OccupiesQuota);
        Assert.Equal(LoadBalancerStatus.Failed, failed.Status);
    }

    // The change in one list: the six report methods answer a bool, the status and the operation replace the two
    // existence axes, the derived transition and the base class's pending operation.
    [Fact]
    public void LoadBalancer_SurfaceOfFrozenCopyAgainstLive_IsExactlyTheChange()
    {
        SignatureDiff diff = SignatureDiff.Between(typeof(Old.LoadBalancer), typeof(LoadBalancer));

        _output.WriteLine($"LoadBalancer surface\n{diff}");
        Assert.Equal(
        [
            "public CreateFailed(Guid, String, String, String): Boolean",
            "public CreateSucceeded(Guid, String): Boolean",
            "public DeleteFailed(Guid, String, String, String): Boolean",
            "public DeleteSucceeded(Guid, String): Boolean",
            "public InstanceStatus: LoadBalancerStatus?",
            "public Operation: LoadBalancerOperation",
            "public Status: LoadBalancerStatus",
            "public UpdateFailed(Guid, String, String, String): Boolean",
            "public UpdateSucceeded(Guid, String): Boolean",
        ], diff.Added);
        Assert.Equal(
        [
            "public CurrentExistence: CurrentExistence",
            "public DesiredExistence: DesiredExistence",
            "public FailOperation(Guid, String, String, String): LoadBalancerOperationKind?",
            "public FinishOperation(Guid, String): LoadBalancerOperationKind?",
            "public OperationFailedAt: DateTimeOffset?",
            "public OperationFailureDetail: String",
            "public OperationFailureReason: String",
            "public Pending: PendingOperation<LoadBalancerOperationKind>",
            "public PendingOperationId: Guid?",
            "public Transition: LoadBalancerTransition?",
        ], diff.Removed);
    }
}
