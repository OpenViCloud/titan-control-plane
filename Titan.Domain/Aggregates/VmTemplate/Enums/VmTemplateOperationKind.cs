namespace Titan.Domain.Aggregates.VmTemplate;

/// The one kind of operation a template can wait on in a cluster (titan-ops architecture/resource-lifecycle.md#kinds): sync is
/// add-only, so there is nothing else to ask for. Stored with the operation's id, as every resource's kind is.
public enum VmTemplateOperationKind
{
    Sync
}
