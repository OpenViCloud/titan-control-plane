using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;
namespace Titan.Domain.Aggregates.VmTemplate;

/// One template in ONE cluster (titan-ops features/catalog/design.md#template-per-cluster): whether that cluster holds
/// the image alias. Incus has no status word for an alias (titan-ops features/catalog/design.md#template-state-is-existence).
/// A child of VmTemplate: "one provider id per cluster" is the template's invariant, so only the root reaches these methods.
public class VmTemplateCluster : ChildEntity
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
    /// Why the last sync gave up (titan-ops architecture/desired-and-current.md#request-failure); Detail is the operator's.
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // The one waiting request, as three columns (titan-ops architecture/resource-lifecycle.md#shape); all null or all
    // set, written by RequestSync and EndRequest only.
    private Guid? PendingRequestId { get; set; }
    private VmTemplateRequestKind? PendingRequestKind { get; set; }
    private DateTimeOffset? PendingRequestAskedAt { get; set; }

    /// The sync this cluster is waiting on, or null. Only the report carrying its id ends it
    /// (titan-ops architecture/resource-lifecycle.md#invariants).
    public PendingRequest<VmTemplateRequestKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<VmTemplateRequestKind>.Of(
                PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

    public VmTemplateTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case VmTemplateRequestKind.Sync:
                    return VmTemplateTransition.Syncing;
                default:
                    return null;
            }
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

    private void SetPending(PendingRequest<VmTemplateRequestKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Refused when the alias is there or a sync is waiting: the handler recognises the repeat first and sends nothing
    /// (titan-ops architecture/resource-lifecycle.md#repeat).
    internal PendingRequest<VmTemplateRequestKind> RequestSync()
    {
        bool syncIsWaiting = PendingRequestId != null;
        bool aliasIsThere = CurrentExistence == CurrentExistence.Exists;
        if (syncIsWaiting)
        {
            throw new BadRequestException("INVALID_STATUS", $"A sync is already waiting in cluster '{ClusterId}'.");
        }
        if (aliasIsThere)
        {
            throw new BadRequestException("INVALID_STATUS", $"The template is already on cluster '{ClusterId}'.");
        }

        PendingRequest<VmTemplateRequestKind> request =
            PendingRequest<VmTemplateRequestKind>.Of(Guid.NewGuid(), VmTemplateRequestKind.Sync, DateTimeOffset.UtcNow);
        DesiredExistence = DesiredExistence.Exists;
        SetPending(request);
        UpdatedAt = DateTimeOffset.UtcNow;
        return request;
    }

    // ---- The report of the sync's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends it.

    /// Ends the waiting sync when the report names it; any other report is ignored and returns null. Done clears an older
    /// failure, Failed records the reason (titan-ops architecture/resource-lifecycle.md#kinds). An alias the worker found
    /// is recorded with Exists whatever the outcome, the first alias kept, as the give-up of a create that finds its
    /// object is for every other kind. Nothing absorbs a report here: a template is never Deleted, so the id check is the
    /// whole guard.
    internal PendingRequest<VmTemplateRequestKind>? EndRequest(Guid requestId, VmTemplateRequestEnd end)
    {
        PendingRequest<VmTemplateRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }

        // Incus names the alias after the template, so every report names the same one; the first recorded stays.
        bool bringsTheAlias = !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheAlias)
        {
            ExternalTemplateId ??= end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }

        if (end.Outcome == RequestOutcome.Done)
        {
            CurrentExistence = CurrentExistence.Exists;
            ClearRequestFailure();
        }
        else
        {
            RequestFailureReason = end.Reason;
            RequestFailureDetail = end.Detail;
            RequestFailedAt = DateTimeOffset.UtcNow;
        }

        SetPending(null);
        UpdatedAt = DateTimeOffset.UtcNow;
        return waiting;
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, ends no
    // request (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler saw the alias. It can only have looked for one whose id Titan holds.
    internal void MarkObserved()
    {
        if (string.IsNullOrEmpty(ExternalTemplateId))
            throw new BadRequestException("PROVIDER_ID_MISSING", "A template with no provider id cannot have been seen on the provider.");

        CurrentExistence = CurrentExistence.Exists;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// A report that names the alias it saw: the old route's Created with no sync waiting. Without the alias the cluster
    /// would read ready yet be unusable: VM create refuses a cluster with no ExternalTemplateId.
    internal void MarkAliasObserved(string externalTemplateId)
    {
        if (string.IsNullOrEmpty(externalTemplateId))
            throw new ArgumentException("The alias seen must be named.", nameof(externalTemplateId));

        ExternalTemplateId ??= externalTemplateId;
        CurrentExistence = CurrentExistence.Exists;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// The reconciler did not find the alias. The reason of the last failed sync stays.
    internal void MarkNotFound()
    {
        CurrentExistence = CurrentExistence.NotFound;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void ClearRequestFailure()
    {
        RequestFailureReason = null;
        RequestFailureDetail = null;
        RequestFailedAt = null;
    }
}
