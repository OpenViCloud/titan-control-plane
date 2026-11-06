using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;
namespace Titan.Domain.Aggregates.VmTemplate;

/// One template in ONE cluster (titan-ops features/catalog/design.md#template-per-cluster): whether that cluster holds
/// the image alias. Incus has no status word for an alias (titan-ops features/catalog/design.md#template-state-is-existence).
/// A child of VmTemplate: "one provider id per cluster" is the template's invariant, so only the root reaches these methods.
public class VmTemplateCluster : ProviderResource<VmTemplateOperationKind>, IEntity
{
    public Guid TemplateId { get; private set; }
    public string ClusterId { get; private set; } = default!;

    /// Observations and a sync's own report write it (titan-ops architecture/desired-and-current.md#existence-axis).
    /// Never Deleted: a template is never removed from a cluster.
    public CurrentExistence CurrentExistence { get; private set; }
    /// Always Exists (add-only sync).
    public DesiredExistence DesiredExistence { get; private set; }
    /// The image alias tpl-<templateId> on this cluster.
    public string? ExternalTemplateId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public VmTemplateTransition? Transition
    {
        get
        {
            switch (PendingOperationKind)
            {
                case VmTemplateOperationKind.Sync:
                    return VmTemplateTransition.Syncing;
                default:
                    return null;
            }
        }
    }

    private VmTemplateCluster() { }

    /// A cluster nothing has been asked of or observed in yet. The root adds an operation or an observation at once.
    internal static VmTemplateCluster NotYetSynced(Guid templateId, string clusterId) => new()
    {
        TemplateId = templateId,
        ClusterId = clusterId,
        CurrentExistence = CurrentExistence.NotCreated,
        DesiredExistence = DesiredExistence.Exists,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Refused when the alias is there or a sync is waiting: the handler recognises the repeat first and sends nothing
    /// (titan-ops architecture/resource-lifecycle.md#repeat).
    internal PendingOperation<VmTemplateOperationKind> Sync()
    {
        bool syncIsWaiting = PendingOperationId != null;
        bool aliasIsThere = CurrentExistence == CurrentExistence.Exists;
        if (syncIsWaiting)
        {
            throw new BusinessException("INVALID_STATUS", $"A sync is already waiting in cluster '{ClusterId}'.");
        }
        if (aliasIsThere)
        {
            throw new BusinessException("INVALID_STATUS", $"The template is already on cluster '{ClusterId}'.");
        }

        PendingOperation<VmTemplateOperationKind> operation = NewOperation(VmTemplateOperationKind.Sync);
        DesiredExistence = DesiredExistence.Exists;
        SetPending(operation);
        UpdatedAt = DateTimeOffset.UtcNow;
        return operation;
    }

    // ---- The report of the sync's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends it.

    /// The sync's command ended Done. Ends the waiting sync when the report names it; any other report is ignored and
    /// returns null. Records the alias with Exists and clears an older failure
    /// (titan-ops architecture/resource-lifecycle.md#kinds). Nothing absorbs a report here: a template is never Deleted,
    /// so the id check is the whole guard.
    internal VmTemplateOperationKind? FinishOperation(Guid operationId, string? externalId)
    {
        PendingOperation<VmTemplateOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        EnsureProviderIdOnDone(ExternalTemplateId, externalId);
        RecordWhatTheWorkerSaw(externalId);

        CurrentExistence = CurrentExistence.Exists;
        ClearFailure();

        SetPending(null);
        UpdatedAt = DateTimeOffset.UtcNow;
        return waiting.Kind;
    }

    /// The sync's command gave up. Ends the waiting sync when the report names it; any other report is ignored and
    /// returns null. Records the reason; an alias the worker found is recorded with Exists, as the give-up of a create
    /// that finds its object is for every other kind (titan-ops architecture/resource-lifecycle.md#kinds).
    internal VmTemplateOperationKind? FailOperation(Guid operationId, string? externalId, string? reason, string? detail)
    {
        PendingOperation<VmTemplateOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RecordWhatTheWorkerSaw(externalId);

        RecordFailure(reason, detail);

        SetPending(null);
        UpdatedAt = DateTimeOffset.UtcNow;
        return waiting.Kind;
    }

    private PendingOperation<VmTemplateOperationKind>? WaitingOperationNamed(Guid operationId)
    {
        PendingOperation<VmTemplateOperationKind>? waiting = Pending;
        bool reportIsForThePendingOperation = waiting != null && waiting.Id == operationId;
        if (!reportIsForThePendingOperation)
        {
            return null;
        }
        return waiting;
    }

    // Incus names the alias after the template, so every report names the same one; the first recorded stays.
    private void RecordWhatTheWorkerSaw(string? externalId)
    {
        bool bringsTheAlias = !string.IsNullOrEmpty(externalId);
        if (!bringsTheAlias)
        {
            return;
        }
        if (ExternalTemplateId == null)
        {
            ExternalTemplateId = externalId;
        }
        CurrentExistence = CurrentExistence.Exists;
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, ends no
    // operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler saw the alias. It can only have looked for one whose id Titan holds.
    internal void MarkObserved()
    {
        if (string.IsNullOrEmpty(ExternalTemplateId))
            throw new BusinessException("PROVIDER_ID_MISSING", "A template with no provider id cannot have been seen on the provider.");

        CurrentExistence = CurrentExistence.Exists;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// The reconciler did not find the alias. The reason of the last failed sync stays.
    internal void MarkNotFound()
    {
        CurrentExistence = CurrentExistence.NotFound;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
