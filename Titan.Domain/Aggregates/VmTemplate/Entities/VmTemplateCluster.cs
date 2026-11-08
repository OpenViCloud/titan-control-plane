using Titan.Domain.Model;
namespace Titan.Domain.Aggregates.VmTemplate;

/// One template in ONE cluster (titan-ops features/catalog/design.md#template-per-cluster): whether that cluster holds
/// the image alias. Incus has no status word for an alias (titan-ops features/catalog/design.md#template-state-is-existence).
/// A child of VmTemplate: "one provider id per cluster" is the template's invariant, so only the root reaches these methods,
/// and handlers go through VmTemplate.SyncIn, SyncSucceededIn, SyncFailedIn, MarkObservedIn and MarkNotFoundIn.
public partial class VmTemplateCluster : IEntity
{
    public Guid TemplateId { get; private set; }
    public string ClusterId { get; private set; } = default!;
    /// The one status the admin sees. Only a sync, the report ending it, or an observation at rest writes it, each
    /// through the moves of VmTemplateCluster.Sync.cs and VmTemplateCluster.Observation.cs.
    public VmTemplateClusterStatus Status { get; private set; }
    /// The sync the cluster waits on, the last one that gave up, and whether the alias was last seen while a sync waits.
    public VmTemplateOperation Operation { get; private set; } = VmTemplateOperation.None;
    /// The image alias tpl-<templateId> on this cluster.
    public string? ExternalTemplateId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private VmTemplateCluster() { }

    /// A cluster nothing has been asked of or observed in yet: it holds no alias. The root adds a sync or an observation at once.
    internal static VmTemplateCluster NotYetSynced(Guid templateId, string clusterId) => new()
    {
        TemplateId = templateId,
        ClusterId = clusterId,
        Status = VmTemplateClusterStatus.NotFound,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// Whether the cluster holds the alias as far as Titan last knew: Available, or null. While a sync waits it is what
    /// the sync last saw; at rest it is Available when the status is. Worked out, never stored.
    public VmTemplateClusterStatus? InstanceStatus
    {
        get
        {
            if (IsTransitionStatus)
            {
                return Operation.LastSeenStatus;
            }
            if (Status == VmTemplateClusterStatus.Available)
            {
                return VmTemplateClusterStatus.Available;
            }
            return null;
        }
    }

    /// A sync waits on its command: only the report carrying its id moves the status on.
    private bool IsTransitionStatus => Status == VmTemplateClusterStatus.Syncing;

    /// What customers in the cluster may pick: the cluster holds the alias, or a re-sync waits and saw it
    /// (titan-ops features/catalog/design.md#template-customer-read). VmTemplateRepository writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsSynced => InstanceStatus == VmTemplateClusterStatus.Available;

    /// The report names the sync the cluster waits on, which its status says.
    private bool IsWaitingOn(Guid operationId, VmTemplateClusterStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    // Incus names the alias after the template, so every report names the same one; the first recorded stays.
    private void RecordTheAlias(string? externalId)
    {
        bool bringsTheAlias = ExternalTemplateId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheAlias)
        {
            ExternalTemplateId = externalId;
        }
    }
}
