using Titan.Domain.Aggregates.VmTemplate;
using Xunit.Abstractions;
using static Titan.Tests.Characterization.MoveSequences;

namespace Titan.Tests.Characterization;

// A template's sync state in one cluster, reached through the root VmTemplate. The adapters below are the extension
// points of the change that reshapes VmTemplateCluster: OldSide keeps calling Old/, CurrentSide is what that change
// re-points (the moves, PendingId and View).
[Trait("Category", "Characterization")]
public class TemplateClusterCharacterization
{
    private const string Cluster = "c1";
    private const string Alias = "tpl-a";
    private static readonly int MaxLength = CharacterizationDepth.For(4);

    private readonly ITestOutputHelper _output;

    public TemplateClusterCharacterization(ITestOutputHelper output) => _output = output;

    /// What a caller can read about the one cluster after a move. Status is the one status: the live aggregate keeps it,
    /// the frozen one is mapped to it by the rule its migration applies to stored rows. Verdict is what the sync runner
    /// makes of the row.
    internal sealed record ClusterView(
        string Row, bool IsSynced, string ProviderId, bool HasPending, bool HasFailureReason,
        string Status, string Verdict, string ExternalTemplateId);

    internal static Side<Old.VmTemplate, ClusterView> OldSide() => new(
        () => Old.VmTemplate.Create("Ubuntu-Web", "desc", "Ubuntu-20.04", 20, "m"),
        OldMoves(),
        ViewOf);

    internal static Side<VmTemplate, ClusterView> CurrentSide() => new(
        () => VmTemplate.Create("Ubuntu-Web", "desc", "Ubuntu-20.04", 20, "m"),
        CurrentMoves(),
        ViewOf);

    // ---- the old aggregate

    private static Guid PendingId(Old.VmTemplate t) =>
        t.Clusters.SingleOrDefault()?.Pending?.Id ?? Guid.NewGuid();

    // The old report methods return the kind of the operation they ended, or null; the live ones return a bool. The
    // observer's question is the same: did the report end the operation.
    private static List<Move<Old.VmTemplate>> OldMoves() =>
    [
        new("Sync", t => Doing(() => t.SyncIn(Cluster))),
        new("DoneNoAlias", t => Returning(() => t.FinishSyncOperation(Cluster, PendingId(t), null) != null)),
        new("DoneAlias", t => Returning(() => t.FinishSyncOperation(Cluster, PendingId(t), Alias) != null)),
        new("FailNoAlias", t => Returning(() => t.FailSyncOperation(Cluster, PendingId(t), null, "reason", "detail") != null)),
        new("FailAlias", t => Returning(() => t.FailSyncOperation(Cluster, PendingId(t), Alias, "reason", "detail") != null)),
        new("DoneForeign", t => Returning(() => t.FinishSyncOperation(Cluster, Guid.NewGuid(), Alias) != null)),
        new("FailForeign", t => Returning(() => t.FailSyncOperation(Cluster, Guid.NewGuid(), Alias, "reason", "detail") != null)),
        new("Observed", t => Doing(() => t.MarkObservedIn(Cluster))),
        new("NotFound", t => Doing(() => t.MarkNotFoundIn(Cluster))),
    ];

    private static ClusterView ViewOf(Old.VmTemplate t)
    {
        Old.VmTemplateCluster? row = t.Clusters.SingleOrDefault();
        return new ClusterView(
            Row: row == null ? "none" : "row",
            IsSynced: t.IsSyncedIn(Cluster),
            ProviderId: Capture(() => t.ProviderIdIn(Cluster)),
            HasPending: row?.Pending != null,
            HasFailureReason: row?.OperationFailureReason != null,
            Status: row == null ? "-" : MappedStatus(row),
            Verdict: row == null ? "-" : VerdictOf(row),
            ExternalTemplateId: row?.ExternalTemplateId ?? "-");
    }

    private static string MappedStatus(Old.VmTemplateCluster row)
    {
        bool noAlias = row.ExternalTemplateId == null;
        bool failed = row.OperationFailureReason != null;
        if (row.Pending != null) return "Syncing";
        if (noAlias && row.CurrentExistence == Old.CurrentExistence.NotFound && !failed) return "NotFound";
        if (noAlias) return "Failed";
        if (row.CurrentExistence == Old.CurrentExistence.NotFound) return "NotFound";
        return "Available";
    }

    private static string VerdictOf(Old.VmTemplateCluster row)
    {
        if (row.CurrentExistence == Old.CurrentExistence.Exists) return "synced";
        if (row.OperationFailureReason != null && row.Pending == null) return "failed";
        return "pending";
    }

    // ---- the live aggregate

    private static Guid PendingId(VmTemplate t) =>
        t.Clusters.SingleOrDefault()?.Operation.PendingId ?? Guid.NewGuid();

    private static List<Move<VmTemplate>> CurrentMoves() =>
    [
        new("Sync", t => Doing(() => t.SyncIn(Cluster))),
        new("DoneNoAlias", t => Returning(() => t.SyncSucceededIn(Cluster, PendingId(t), null))),
        new("DoneAlias", t => Returning(() => t.SyncSucceededIn(Cluster, PendingId(t), Alias))),
        new("FailNoAlias", t => Returning(() => t.SyncFailedIn(Cluster, PendingId(t), null, "reason", "detail"))),
        new("FailAlias", t => Returning(() => t.SyncFailedIn(Cluster, PendingId(t), Alias, "reason", "detail"))),
        new("DoneForeign", t => Returning(() => t.SyncSucceededIn(Cluster, Guid.NewGuid(), Alias))),
        new("FailForeign", t => Returning(() => t.SyncFailedIn(Cluster, Guid.NewGuid(), Alias, "reason", "detail"))),
        new("Observed", t => Doing(() => t.MarkObservedIn(Cluster))),
        new("NotFound", t => Doing(() => t.MarkNotFoundIn(Cluster))),
    ];

    private static ClusterView ViewOf(VmTemplate t)
    {
        VmTemplateCluster? row = t.Clusters.SingleOrDefault();
        return new ClusterView(
            Row: row == null ? "none" : "row",
            IsSynced: t.IsSyncedIn(Cluster),
            ProviderId: Capture(() => t.ProviderIdIn(Cluster)),
            HasPending: row?.Operation.PendingId != null,
            HasFailureReason: row?.Operation.FailureReason != null,
            Status: row?.Status.ToString() ?? "-",
            Verdict: row == null ? "-" : VerdictOf(row),
            ExternalTemplateId: row?.ExternalTemplateId ?? "-");
    }

    private static string VerdictOf(VmTemplateCluster row)
    {
        if (row.IsSynced) return "synced";
        if (row.Operation.FailureReason != null && row.Status != VmTemplateClusterStatus.Syncing) return "failed";
        return "pending";
    }

    // ---- tests

    [Fact]
    public void TemplateCluster_FrozenCopyAgainstLiveAggregate_NoSequenceDiffers()
    {
        CharacterizationReport report = Compare(OldSide(), CurrentSide(), MaxLength);

        _output.WriteLine($"VmTemplateCluster: {report.Summary}");
        Assert.Equal(Count(report.AlphabetSize, MaxLength), report.SequencesRun);
        report.AssertNoMismatches();
    }

    // The change in one list: the three report methods answer a bool, the status and the operation replace the two
    // existence axes, the derived transition and the base class's pending operation.
    [Fact]
    public void TemplateCluster_SurfaceOfFrozenCopyAgainstLive_IsExactlyTheChange()
    {
        SignatureDiff root = SignatureDiff.Between(typeof(Old.VmTemplate), typeof(VmTemplate));
        SignatureDiff entity = SignatureDiff.Between(typeof(Old.VmTemplateCluster), typeof(VmTemplateCluster));

        _output.WriteLine($"VmTemplate surface\n{root}\nVmTemplateCluster surface\n{entity}");
        Assert.Equal(
        [
            "public SyncFailedIn(String, Guid, String, String, String): Boolean",
            "public SyncIn(String): Guid",
            "public SyncSucceededIn(String, Guid, String): Boolean",
        ], root.Added);
        Assert.Equal(
        [
            "public FailSyncOperation(String, Guid, String, String, String): VmTemplateOperationKind?",
            "public FinishSyncOperation(String, Guid, String): VmTemplateOperationKind?",
            "public SyncIn(String): PendingOperation<VmTemplateOperationKind>",
        ], root.Removed);
        Assert.Equal(
        [
            "internal Sync(): Guid",
            "internal SyncFailed(Guid, String, String, String): Boolean",
            "internal SyncSucceeded(Guid, String): Boolean",
            "public InstanceStatus: VmTemplateClusterStatus?",
            "public IsSynced: Boolean",
            "public Operation: VmTemplateOperation",
            "public Status: VmTemplateClusterStatus",
        ], entity.Added);
        Assert.Equal(
        [
            "internal FailOperation(Guid, String, String, String): VmTemplateOperationKind?",
            "internal FinishOperation(Guid, String): VmTemplateOperationKind?",
            "internal Sync(): PendingOperation<VmTemplateOperationKind>",
            "public CurrentExistence: CurrentExistence",
            "public DesiredExistence: DesiredExistence",
            "public OperationFailedAt: DateTimeOffset?",
            "public OperationFailureDetail: String",
            "public OperationFailureReason: String",
            "public Pending: PendingOperation<VmTemplateOperationKind>",
            "public PendingOperationId: Guid?",
            "public Transition: VmTemplateTransition?",
        ], entity.Removed);
    }
}
