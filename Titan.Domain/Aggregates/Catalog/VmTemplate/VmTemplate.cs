using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;
namespace Titan.Domain.Aggregates.Catalog.VmTemplate;

// A template answers "which operating system", never "how big" (titan-ops features/catalog/design.md#vm-offering-sizing).
// It points at an offering (VmOfferingId) for its base sizing instead of owning sizing numbers: a tier id cannot drift
// (titan-ops features/catalog/design.md#template-borrows-sizing).
public class VmTemplate : AggregateRoot
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
    public DateTimeOffset UpdatedAt { get; private set; }

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
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public VmTemplateCluster? ClusterOrNull(string clusterId)
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
        VmTemplateCluster? cluster = ClusterOrNull(clusterId);
        if (cluster == null)
        {
            return false;
        }
        return cluster.CurrentExistence == CurrentExistence.Exists;
    }

    /// The image alias a VM in that cluster is created from, handed out only past the check
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    public string ProviderIdIn(string clusterId)
    {
        VmTemplateCluster? cluster = ClusterOrNull(clusterId);
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
        bool aliasIsThere = cluster!.CurrentExistence == CurrentExistence.Exists;
        if (!aliasIsThere)
        {
            throw new BusinessException("TEMPLATE_UNAVAILABLE",
                $"Template '{Name}' is not available in cluster '{clusterId}' right now.");
        }
        return externalTemplateId;
    }

    /// Refused when that cluster has the alias or a sync waiting: the handler recognises the repeat first and
    /// re-sends the command (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public void RequestSyncIn(string clusterId)
    {
        RowFor(clusterId).RequestSync();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// Observations converge (titan-ops architecture/intent-and-observation.md#observation-converges): a report for a
    /// cluster with no row yet records what was observed there instead of refusing it. Which clusters exist is the
    /// handler's check, not the aggregate's. Returns whether it ended a waiting sync.
    public bool MarkCreatedIn(string clusterId, string externalTemplateId)
    {
        bool fulfilled = RowFor(clusterId).MarkCreated(externalTemplateId);
        UpdatedAt = DateTimeOffset.UtcNow;
        return fulfilled;
    }

    public void MarkObservedIn(string clusterId)
    {
        RowFor(clusterId).MarkObserved();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkNotFoundIn(string clusterId)
    {
        RowFor(clusterId).MarkNotFound();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// Returns whether a waiting sync took the failure; a stale give-up records nothing.
    public bool MarkRequestFailedIn(string clusterId, string? reason, string? detail)
    {
        bool recorded = RowFor(clusterId).MarkRequestFailed(reason, detail);
        UpdatedAt = DateTimeOffset.UtcNow;
        return recorded;
    }

    private VmTemplateCluster RowFor(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("clusterId must not be empty", nameof(clusterId));

        VmTemplateCluster? existing = ClusterOrNull(clusterId);
        if (existing != null)
        {
            return existing;
        }
        var added = VmTemplateCluster.NotYetSynced(Id, clusterId);
        _clusters.Add(added);
        return added;
    }
}
