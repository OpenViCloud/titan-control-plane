using Titan.Domain.Model;
namespace Titan.Domain.Aggregates.FunctionRuntime;

/// A runtime of OVC Function, global (no tenant): the image a customer's function runs in
/// (titan-ops features/ovc-function/design.md#runtimes). The catalog follows the VM template's: seeded rows, no admin
/// CRUD, and one sync state per function cluster.
public class FunctionRuntime : IAggregateRoot
{
    public Guid Id { get; private set; }
    /// The language and its version, as customers pick it ("python3.15", "nodejs26", "go").
    public string Name { get; private set; } = default!;
    /// The GHCR reference, pinned by digest: the digest is the runtime's id on a cluster, so a tag would let the image
    /// change under functions already deployed on it.
    public string Image { get; private set; } = default!;
    public string Architecture { get; private set; } = default!;
    /// Customers see and may pick only listed runtimes. Create lists a runtime; only the seed unlists one.
    public bool IsListed { get; private set; }
    /// The last day the language's own project supports this version; null when it names none (go).
    public DateOnly? SupportEndsOn { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<FunctionRuntimeCluster> _clusters = [];
    public IReadOnlyCollection<FunctionRuntimeCluster> Clusters => _clusters;

    private FunctionRuntime() { }

    public static FunctionRuntime Create(string name, string image, string architecture, DateOnly? supportEndsOn)
    {
        // For tests: the catalog is seeded by migration, not made here. The worker checks the image's full form.
        if (string.IsNullOrWhiteSpace(image) || !image.Contains("@sha256:", StringComparison.Ordinal))
            throw new ArgumentException("image must be pinned by digest", nameof(image));

        return new FunctionRuntime
        {
            Id = Guid.NewGuid(),
            Name = name,
            Image = image,
            Architecture = architecture,
            IsListed = true,
            SupportEndsOn = supportEndsOn,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private FunctionRuntimeCluster? FindCluster(string clusterId)
    {
        foreach (FunctionRuntimeCluster cluster in _clusters)
        {
            if (cluster.ClusterId == clusterId)
            {
                return cluster;
            }
        }
        return null;
    }

    /// Nothing holds or waits in that cluster: no row, its sync failed, or its image went missing. SyncIn refuses the
    /// rest, so a sync-all asks only these (titan-ops architecture/resource-lifecycle.md#repeat).
    public bool NeedsSyncIn(string clusterId)
    {
        FunctionRuntimeCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return true;
        }
        return cluster.Status == FunctionRuntimeClusterStatus.Failed || cluster.Status == FunctionRuntimeClusterStatus.NotFound;
    }

    /// What a new function version may be deployed with in that cluster: listed, every Ready node holds the image, and
    /// the language still supports it on that day. A version already running keeps running past the day (titan-ops
    /// features/ovc-function/design.md#runtimes).
    public bool IsUsableIn(string clusterId, DateOnly today)
    {
        if (!IsListed)
        {
            return false;
        }
        if (SupportEndsOn != null && today > SupportEndsOn.Value)
        {
            return false;
        }
        FunctionRuntimeCluster? cluster = FindCluster(clusterId);
        return cluster != null && cluster.IsAvailable;
    }

    /// What a new version may be packed on: listed, and the language still supports it on that day
    /// (titan-ops features/ovc-function/design.md#runtimes).
    public bool TakesNewVersionsOn(DateOnly today)
    {
        if (!IsListed)
        {
            return false;
        }
        return SupportEndsOn == null || today <= SupportEndsOn.Value;
    }

    /// Every Ready node of that cluster holds the image as far as Titan last knew: what a deploy needs, whatever the day.
    /// A version packed before the support date may be deployed again after it (titan-ops features/ovc-function/design.md#runtimes).
    public bool IsAvailableIn(string clusterId)
    {
        FunctionRuntimeCluster? cluster = FindCluster(clusterId);
        return cluster != null && cluster.IsAvailable;
    }

    /// Refused when that cluster holds the image or a sync waits. Returns the sync's id for the command's key and
    /// payload.
    public Guid SyncIn(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("clusterId must not be empty", nameof(clusterId));
        FunctionRuntimeCluster? existing = FindCluster(clusterId);
        if (existing != null)
        {
            return existing.Sync();
        }
        var added = FunctionRuntimeCluster.FirstSync(Id, clusterId);
        _clusters.Add(added);
        return added.Operation.PendingId!.Value;
    }

    /// The report that a sync's command in one cluster ended Done (titan-ops architecture/resource-lifecycle.md#shape):
    /// ends that cluster's waiting sync when the report names it, else returns false and changes nothing. A cluster
    /// with no row has nothing waiting, so no row is added for it.
    public bool SyncSucceededIn(string clusterId, Guid operationId)
    {
        FunctionRuntimeCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return false;
        }
        return cluster.SyncSucceeded(operationId);
    }

    public bool SyncFailedIn(string clusterId, Guid operationId, string? reason, string? detail)
    {
        FunctionRuntimeCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return false;
        }
        return cluster.SyncFailed(operationId, reason, detail);
    }

    // Observations converge (titan-ops architecture/intent-and-observation.md#observation-converges): a report for a
    // cluster with no row records what was observed there. Which clusters exist is the handler's check. None ends a
    // sync.

    public void MarkObservedIn(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("clusterId must not be empty", nameof(clusterId));
        FunctionRuntimeCluster? existing = FindCluster(clusterId);
        if (existing == null)
        {
            _clusters.Add(FunctionRuntimeCluster.Observed(Id, clusterId, exists: true));
            return;
        }
        existing.MarkObserved();
    }

    public void MarkNotFoundIn(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("clusterId must not be empty", nameof(clusterId));
        FunctionRuntimeCluster? existing = FindCluster(clusterId);
        if (existing == null)
        {
            _clusters.Add(FunctionRuntimeCluster.Observed(Id, clusterId, exists: false));
            return;
        }
        existing.MarkNotFound();
    }
}
