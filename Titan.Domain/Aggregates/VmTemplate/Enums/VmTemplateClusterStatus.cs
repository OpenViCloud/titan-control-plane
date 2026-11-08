namespace Titan.Domain.Aggregates.VmTemplate;

/// The one status of a template in one cluster (titan-ops features/catalog/design.md#template-state-is-existence). Incus has
/// no status word for an image alias, only whether the cluster holds it: Syncing says the sync waits, Available and
/// NotFound say whether the cluster holds the alias at rest, and Failed says a sync gave up before any alias was there.
/// Never Deleted (a template is not removed from a cluster) and never Creating (a first ask goes straight to Syncing).
public enum VmTemplateClusterStatus
{
    Syncing,
    // The cluster holds the alias and nothing waits.
    Available,
    // The cluster does not hold the alias: the reconciler did not find it, or an observation found nothing where no sync ran.
    NotFound,
    // The sync gave up before the cluster held any alias.
    Failed
}
