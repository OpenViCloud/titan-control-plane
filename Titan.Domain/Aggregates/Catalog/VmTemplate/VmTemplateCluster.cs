using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;
namespace Titan.Domain.Aggregates.Catalog.VmTemplate;

/// One template in ONE cluster (titan-ops features/catalog/design.md#template-per-cluster): whether that cluster holds
/// the image alias. Incus has no status word for an alias (titan-ops features/catalog/design.md#template-state-is-existence).
/// A child of VmTemplate: "one provider id per cluster" is the template's invariant, so only the root reaches these methods.
public class VmTemplateCluster : ChildEntity
{
    public Guid TemplateId { get; private set; }
    public string ClusterId { get; private set; } = default!;

    /// Only observations write it (titan-ops architecture/desired-and-current.md#existence-axis). Never Deleted: a
    /// template is never removed from a cluster.
    public CurrentExistence CurrentExistence { get; private set; }
    /// Always Exists (add-only sync).
    public DesiredExistence DesiredExistence { get; private set; }
    /// The sync request still waiting for the worker.
    public Guid? PendingIntentId { get; private set; }
    /// The image alias tpl-<templateId> on this cluster.
    public string? ExternalTemplateId { get; private set; }
    /// Why the last sync gave up (titan-ops architecture/desired-and-current.md#request-failure); Detail is the operator's.
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public VmTemplateTransition? Transition
    {
        get
        {
            bool nothingIsWaiting = PendingIntentId == null;
            if (nothingIsWaiting)
            {
                return null;
            }
            return VmTemplateTransition.Syncing;
        }
    }

    private VmTemplateCluster() { }

    /// A cluster nothing has been asked of or observed in yet. The root adds a request or an observation at once.
    internal static VmTemplateCluster NotYetSynced(Guid templateId, string clusterId) => new()
    {
        TemplateId = templateId,
        ClusterId = clusterId,
        CurrentExistence = CurrentExistence.NotCreated,
        DesiredExistence = DesiredExistence.Exists,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// Refused when the alias is there or a sync is waiting: the handler recognises the repeat first and re-sends
    /// the command (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    internal void RequestSync()
    {
        bool syncIsWaiting = PendingIntentId != null;
        bool aliasIsThere = CurrentExistence == CurrentExistence.Exists;
        if (syncIsWaiting)
        {
            throw new BadRequestException("INVALID_STATUS", $"A sync is already waiting in cluster '{ClusterId}'.");
        }
        if (aliasIsThere)
        {
            throw new BadRequestException("INVALID_STATUS", $"The template is already on cluster '{ClusterId}'.");
        }

        DesiredExistence = DesiredExistence.Exists;
        PendingIntentId = Guid.NewGuid();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// The sync command found or pulled the alias. Without the alias the cluster would read ready yet be unusable:
    /// VM create refuses a cluster with no ExternalTemplateId. Returns whether it ended a waiting sync.
    internal bool MarkCreated(string externalTemplateId)
    {
        if (string.IsNullOrEmpty(externalTemplateId))
            throw new BadRequestException("INVALID_STATUS", "ExternalId is required when the report is Created.");

        bool fulfilled = PendingIntentId != null;
        if (ExternalTemplateId == null)
        {
            ExternalTemplateId = externalTemplateId;
        }
        CurrentExistence = CurrentExistence.Exists;
        PendingIntentId = null;
        ClearRequestFailure();
        UpdatedAt = DateTimeOffset.UtcNow;
        return fulfilled;
    }

    /// The reconciler saw the alias. It can only have looked for one whose id Titan holds.
    internal void MarkObserved()
    {
        if (string.IsNullOrEmpty(ExternalTemplateId))
            throw new BadRequestException("PROVIDER_ID_MISSING", "A template with no provider id cannot have been seen on the provider.");

        CurrentExistence = CurrentExistence.Exists;
        PendingIntentId = null;
        ClearRequestFailure();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// The reconciler did not find the alias. A waiting sync ends; the reason of the last failed sync stays.
    internal void MarkNotFound()
    {
        CurrentExistence = CurrentExistence.NotFound;
        PendingIntentId = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// The sync gave up. With nothing waiting the report is stale and records nothing.
    internal bool MarkRequestFailed(string? reason, string? detail)
    {
        bool syncIsWaiting = PendingIntentId != null;
        if (!syncIsWaiting)
        {
            return false;
        }
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
        PendingIntentId = null;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private void ClearRequestFailure()
    {
        RequestFailureReason = null;
        RequestFailureDetail = null;
        RequestFailedAt = null;
    }
}
