namespace Titan.Application.Abstractions;

// Records member-initiated activity events for the tenant Dashboard feed. Resolves the acting
// tenant + user from the current request context, so call sites only say WHAT happened.
public interface IActivityRecorder
{
    // Stage an event to be flushed by the caller's own next SaveChanges (shared DbContext). Use on
    // write paths that already persist a sibling change (e.g. VM insert) so the two are atomic.
    void Stage(string action, string resourceType, Guid resourceId, string resourceName);

    // Stage + flush immediately, for a path with no sibling DB write of its own.
    Task RecordAsync(string action, string resourceType, Guid resourceId, string resourceName, CancellationToken ct = default);

    // An outcome event (titan-ops features/activity/design.md#request-and-outcome-names), observed by a callback or
    // the reconciler. Those callers act for no tenant and no user, so the tenant is the resource's own and
    // the actor is "system". Flushed by the caller's SaveChanges.
    void StageOutcome(Guid tenantId, string action, string resourceType, Guid resourceId, string resourceName);
}
