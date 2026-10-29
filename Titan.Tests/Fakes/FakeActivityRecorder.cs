using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// Capturing stub for IActivityRecorder — records what each handler staged/recorded so tests can
// assert "this action was logged" without a DbContext. Stub-first (no Moq): the assertion is on
// captured content, not call setup.
public class FakeActivityRecorder : IActivityRecorder
{
    // OutcomeTenantId is set only for outcome events (titan-ops features/activity/design.md#request-and-outcome-names): the tenant comes from the
    // resource, because the callback that observes the outcome has no current user.
    public record Entry(string Action, string ResourceType, Guid ResourceId, string ResourceName, bool Saved,
        Guid? OutcomeTenantId = null);

    public List<Entry> Events { get; } = new();

    public void Stage(string action, string resourceType, Guid resourceId, string resourceName) =>
        Events.Add(new(action, resourceType, resourceId, resourceName, Saved: false));

    public Task RecordAsync(string action, string resourceType, Guid resourceId, string resourceName, CancellationToken ct = default)
    {
        Events.Add(new(action, resourceType, resourceId, resourceName, Saved: true));
        return Task.CompletedTask;
    }

    public void StageOutcome(Guid tenantId, string action, string resourceType, Guid resourceId, string resourceName) =>
        Events.Add(new(action, resourceType, resourceId, resourceName, Saved: false, OutcomeTenantId: tenantId));
}
