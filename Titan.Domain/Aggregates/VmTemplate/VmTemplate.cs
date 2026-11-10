using Titan.Domain.Exceptions;
using Titan.Domain.Model;
namespace Titan.Domain.Aggregates.VmTemplate;

// A template answers "which operating system", never "how big" (titan-ops features/catalog/design.md#vm-offering-sizing).
// It points at an offering (VmOfferingId) for its base sizing instead of owning sizing numbers: a tier id cannot drift
// (titan-ops features/catalog/design.md#template-borrows-sizing).
public class VmTemplate : IAggregateRoot
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public string OsImageName { get; private set; } = default!;

    /// The distro's own cloud image, with "{arch}" where the distro names the architecture; null ⇒ the worker
    /// pulls OsImageName from the image server (titan-ops features/catalog/design.md#template-is-an-image-alias).
    /// Set by the seed only.
    public string? ImageUrl { get; private set; }

    /// Customers see and may pick only listed templates (titan-ops features/catalog/design.md#template-listed).
    /// Create lists a template; only the seed unlists one.
    public bool IsListed { get; private set; }
    public int? RootDiskSizeGb { get; private set; }

    /// The base sizing the provider stores on the template, not what a VM gets: the customer's offering
    /// overrides it at instantiate, so nothing here constrains what the customer may choose.
    public string VmOfferingId { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// One sync state per cluster the template was asked into or observed in
    /// (titan-ops features/catalog/design.md#template-per-cluster).
    private readonly List<VmTemplateCluster> _clusters = [];
    public IReadOnlyCollection<VmTemplateCluster> Clusters => _clusters;

    private VmTemplate() { }

    public static VmTemplate Create(string name, string? description, string osImageName,
        int? rootDiskSizeGb, string vmOfferingId)
    {
        // The FK rejects an unknown id but accepts "", which would reach the sync path as a lookup miss far
        // from the row that caused it.
        if (string.IsNullOrWhiteSpace(vmOfferingId))
            throw new ArgumentException("vmOfferingId must not be empty", nameof(vmOfferingId));

        return new VmTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            OsImageName = osImageName,
            RootDiskSizeGb = rootDiskSizeGb,
            VmOfferingId = vmOfferingId,
            IsListed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private VmTemplateCluster? FindCluster(string clusterId)
    {
        foreach (VmTemplateCluster cluster in _clusters)
        {
            if (cluster.ClusterId == clusterId)
            {
                return cluster;
            }
        }
        return null;
    }

    /// What customers in that cluster may pick: the template whose alias that cluster was last seen to hold.
    public bool IsSyncedIn(string clusterId)
    {
        VmTemplateCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return false;
        }
        return cluster.IsSynced;
    }

    /// Nothing holds or waits in that cluster: it has no row, its sync failed, or its alias went missing. SyncIn refuses
    /// the rest, so a sync-all asks only these (titan-ops architecture/resource-lifecycle.md#repeat).
    public bool NeedsSyncIn(string clusterId)
    {
        VmTemplateCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return true;
        }
        return cluster.Status == VmTemplateClusterStatus.Failed || cluster.Status == VmTemplateClusterStatus.NotFound;
    }

    /// The image alias a VM in that cluster is created from, handed out only past the check
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    public string ProviderIdIn(string clusterId)
    {
        VmTemplateCluster? cluster = FindCluster(clusterId);
        string? externalTemplateId = null;
        if (cluster != null)
        {
            externalTemplateId = cluster.ExternalTemplateId;
        }
        if (string.IsNullOrEmpty(externalTemplateId))
        {
            throw new BusinessException("TEMPLATE_NOT_SYNCED",
                $"Template '{Name}' has not been synced to cluster '{clusterId}' yet.");
        }
        // An alias that went missing keeps its provider id, so the check above lets it through.
        if (!cluster!.IsSynced)
        {
            throw new BusinessException("TEMPLATE_UNAVAILABLE",
                $"Template '{Name}' is not available in cluster '{clusterId}' right now.");
        }
        return externalTemplateId;
    }

    /// Refused when that cluster has the alias or a sync waiting: the handler recognises the repeat first and sends
    /// nothing (titan-ops architecture/resource-lifecycle.md#repeat). The sync has no provider id to hand out yet (the
    /// worker names the alias), so the caller gets the sync's id for the command's key and payload.
    public Guid SyncIn(string clusterId)
    {
        return GetOrAddCluster(clusterId).Sync();
    }

    /// The report that a sync's command in one cluster ended Done (titan-ops architecture/resource-lifecycle.md#shape):
    /// ends that cluster's waiting sync when the report names it, else returns false and changes nothing. A cluster with
    /// no row has nothing waiting, so no row is added for it.
    public bool SyncSucceededIn(string clusterId, Guid operationId, string? externalId)
    {
        VmTemplateCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return false;
        }
        return cluster.SyncSucceeded(operationId, externalId);
    }

    /// The report that a sync's command in one cluster gave up: ends that cluster's waiting sync and records the reason
    /// when the report names it, else returns false and changes nothing. A cluster with no row has nothing waiting.
    public bool SyncFailedIn(string clusterId, Guid operationId, string? externalId, string? reason, string? detail)
    {
        VmTemplateCluster? cluster = FindCluster(clusterId);
        if (cluster == null)
        {
            return false;
        }
        return cluster.SyncFailed(operationId, externalId, reason, detail);
    }

    // Observations converge (titan-ops architecture/intent-and-observation.md#observation-converges): a report for a
    // cluster with no row yet records what was observed there instead of refusing it. Which clusters exist is the
    // handler's check, not the aggregate's. None ends a sync.

    public void MarkObservedIn(string clusterId)
    {
        GetOrAddCluster(clusterId).MarkObserved();
    }

    public void MarkNotFoundIn(string clusterId)
    {
        GetOrAddCluster(clusterId).MarkNotFound();
    }

    private VmTemplateCluster GetOrAddCluster(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("clusterId must not be empty", nameof(clusterId));

        VmTemplateCluster? existing = FindCluster(clusterId);
        if (existing != null)
        {
            return existing;
        }
        var added = VmTemplateCluster.NotYetSynced(Id, clusterId);
        _clusters.Add(added);
        return added;
    }
}
