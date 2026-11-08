using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;
using Xunit.Abstractions;
using static Titan.Tests.Characterization.MoveSequences;

namespace Titan.Tests.Characterization;

// A load balancer root with two listener specs and two backends. The adapters below are the extension points of the
// change that reshapes LoadBalancer: OldSide keeps calling Old/, CurrentSide is what that change re-points (the moves,
// PendingId and View). The initial state is a created load balancer waiting on Incus; the moves reach every other state.
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

    /// What a caller can read about the load balancer after a move. Existence, Desired and Transition are the tokens the
    /// two-axis model exposes; the one-status model must derive the same words.
    internal sealed record LoadBalancerView(
        bool OccupiesQuota, bool IsDeleted, bool IsDeletedOrDeleting, bool HasSpecA, bool HasSpecB,
        string Existence, string Desired, string Transition, bool HasPending, bool HasFailureReason,
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

    private static List<Move<Old.LoadBalancer>> OldMoves() =>
    [
        new("UpdateSame", lb => Returning(() => lb.Update(ListenersA, BackendsA)?.ProviderId)),
        new("UpdateOther", lb => Returning(() => lb.Update(ListenersB, BackendsB)?.ProviderId)),
        new("Delete", lb => Returning(() => lb.Delete()?.ProviderId)),
        new("Done", lb => Returning(() => lb.FinishOperation(PendingId(lb), null))),
        new("DoneVip", lb => Returning(() => lb.FinishOperation(PendingId(lb), VipAddress))),
        new("DoneForeign", lb => Returning(() => lb.FinishOperation(Guid.NewGuid(), VipAddress))),
        new("Fail", lb => Returning(() => lb.FailOperation(PendingId(lb), null, "reason", "detail"))),
        new("FailVip", lb => Returning(() => lb.FailOperation(PendingId(lb), VipAddress, "reason", "detail"))),
        new("FailForeign", lb => Returning(() => lb.FailOperation(Guid.NewGuid(), VipAddress, "reason", "detail"))),
        new("Observed", lb => Doing(lb.MarkObserved)),
        new("NotFound", lb => Doing(lb.MarkNotFound)),
        new("Rename", lb => Doing(() => lb.Rename("renamed"))),
    ];

    private static LoadBalancerView ViewOf(Old.LoadBalancer lb) => new(
        OccupiesQuota: lb.OccupiesQuota,
        IsDeleted: lb.IsDeleted,
        IsDeletedOrDeleting: lb.IsDeletedOrDeleting,
        HasSpecA: lb.HasDesiredSpec(ListenersA, FirstOnly),
        HasSpecB: lb.HasDesiredSpec(ListenersB, BothVms),
        Existence: lb.CurrentExistence.ToString(),
        Desired: lb.DesiredExistence.ToString(),
        Transition: lb.Transition?.ToString() ?? "-",
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

    // ---- the live aggregate

    private static Guid PendingId(LoadBalancer lb) => lb.Pending?.Id ?? Guid.NewGuid();

    private static List<Move<LoadBalancer>> CurrentMoves() =>
    [
        new("UpdateSame", lb => Returning(() => lb.Update(ListenersA, BackendsA)?.ProviderId)),
        new("UpdateOther", lb => Returning(() => lb.Update(ListenersB, BackendsB)?.ProviderId)),
        new("Delete", lb => Returning(() => lb.Delete()?.ProviderId)),
        new("Done", lb => Returning(() => lb.FinishOperation(PendingId(lb), null))),
        new("DoneVip", lb => Returning(() => lb.FinishOperation(PendingId(lb), VipAddress))),
        new("DoneForeign", lb => Returning(() => lb.FinishOperation(Guid.NewGuid(), VipAddress))),
        new("Fail", lb => Returning(() => lb.FailOperation(PendingId(lb), null, "reason", "detail"))),
        new("FailVip", lb => Returning(() => lb.FailOperation(PendingId(lb), VipAddress, "reason", "detail"))),
        new("FailForeign", lb => Returning(() => lb.FailOperation(Guid.NewGuid(), VipAddress, "reason", "detail"))),
        new("Observed", lb => Doing(lb.MarkObserved)),
        new("NotFound", lb => Doing(lb.MarkNotFound)),
        new("Rename", lb => Doing(() => lb.Rename("renamed"))),
    ];

    private static LoadBalancerView ViewOf(LoadBalancer lb) => new(
        OccupiesQuota: lb.OccupiesQuota,
        IsDeleted: lb.IsDeleted,
        IsDeletedOrDeleting: lb.IsDeletedOrDeleting,
        HasSpecA: lb.HasDesiredSpec(ListenersA, FirstOnly),
        HasSpecB: lb.HasDesiredSpec(ListenersB, BothVms),
        Existence: lb.CurrentExistence.ToString(),
        Desired: lb.DesiredExistence.ToString(),
        Transition: lb.Transition?.ToString() ?? "-",
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

    // ---- tests

    [Fact]
    public void LoadBalancer_FrozenCopyAgainstLiveAggregate_NoSequenceDiffers()
    {
        CharacterizationReport report = Compare(OldSide(), CurrentSide(), MaxLength);

        _output.WriteLine($"LoadBalancer: {report.Summary}");
        Assert.Equal(Count(report.AlphabetSize, MaxLength), report.SequencesRun);
        report.AssertNoMismatches();
    }

    [Fact]
    public void LoadBalancer_SurfaceOfFrozenCopyAgainstLive_IsUnchanged()
    {
        SignatureDiff diff = SignatureDiff.Between(typeof(Old.LoadBalancer), typeof(LoadBalancer));

        _output.WriteLine($"LoadBalancer surface\n{diff}");
        Assert.True(diff.IsEmpty, diff.ToString());
        Assert.Contains("public FinishOperation(Guid, String): LoadBalancerOperationKind?", SignatureDiff.Surface(typeof(LoadBalancer)));
    }
}
