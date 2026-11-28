using Titan.Domain.Model;
namespace Titan.Domain.Aggregates.FunctionRuntime;

/// One function runtime in ONE function cluster (titan-ops features/ovc-function/design.md#runtimes): whether every Ready
/// node of that cluster holds the runtime image. No provider id: the image, pinned by digest, is the runtime's id on the
/// cluster. A child of FunctionRuntime, reached only through FunctionRuntime.SyncIn, SyncSucceededIn, SyncFailedIn,
/// MarkObservedIn and MarkNotFoundIn, like VmTemplateCluster.
public partial class FunctionRuntimeCluster : IEntity
{
    public Guid RuntimeId { get; private set; }
    public string ClusterId { get; private set; } = default!;
    /// The one status the admin sees. Only a sync, the report ending it, or an observation at rest writes it, each
    /// through the moves of FunctionRuntimeCluster.Sync.cs and FunctionRuntimeCluster.Observation.cs.
    public FunctionRuntimeClusterStatus Status { get; private set; }
    public FunctionRuntimeOperation Operation { get; private set; } = FunctionRuntimeOperation.None;

    private FunctionRuntimeCluster() { }

    /// The first sync of a cluster with no row: nothing was ever seen there.
    internal static FunctionRuntimeCluster FirstSync(Guid runtimeId, string clusterId) => new()
    {
        RuntimeId = runtimeId,
        ClusterId = clusterId,
        Status = FunctionRuntimeClusterStatus.Syncing,
        Operation = FunctionRuntimeOperation.None.Asked(Guid.NewGuid(), DateTimeOffset.UtcNow, null),
    };

    /// A cluster with no row that the reconciler reports on (titan-ops architecture/intent-and-observation.md#observation-converges).
    internal static FunctionRuntimeCluster Observed(Guid runtimeId, string clusterId, bool exists) => new()
    {
        RuntimeId = runtimeId,
        ClusterId = clusterId,
        Status = exists ? FunctionRuntimeClusterStatus.Available : FunctionRuntimeClusterStatus.NotFound,
    };

    /// Every Ready node holds the image as far as Titan last knew: Available at rest, or a sync waits and saw it. What a
    /// deploy may use, and what the reconciler is told Titan recorded.
    public bool IsAvailable =>
        Status == FunctionRuntimeClusterStatus.Available
        || (Status == FunctionRuntimeClusterStatus.Syncing && Operation.LastSeenStatus == FunctionRuntimeClusterStatus.Available);

    private bool SyncWaits => Status == FunctionRuntimeClusterStatus.Syncing;

    private bool IsWaitingOn(Guid operationId, FunctionRuntimeClusterStatus waiting)
    {
        return Status == waiting && Operation.PendingId == operationId;
    }
}
