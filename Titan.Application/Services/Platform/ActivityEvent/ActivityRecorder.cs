using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.ActivityEvent;
using Titan.Domain.Repositories;
using ActivityEventAggregate = Titan.Domain.Aggregates.ActivityEvent.ActivityEvent;

namespace Titan.Application.Services.Platform.ActivityEvent;

public class ActivityRecorder(IActivityEventRepository repo, ICurrentUser currentUser) : IActivityRecorder
{
    public void Stage(string action, string resourceType, Guid resourceId, string resourceName) =>
        repo.Add(Build(action, resourceType, resourceId, resourceName));

    public async Task RecordAsync(string action, string resourceType, Guid resourceId, string resourceName, CancellationToken ct = default)
    {
        Stage(action, resourceType, resourceId, resourceName);
        await repo.SaveAsync(ct);
    }

    public void StageOutcome(Guid tenantId, string action, string resourceType, Guid resourceId, string resourceName) =>
        // No currentUser here on purpose: the internal callbacks that call this are AllowAnonymous and
        // resolve no tenant, so RequireTenantId() would throw for every outcome.
        repo.Add(ActivityEventAggregate.Create(tenantId, action, resourceType, resourceId, resourceName, SystemActor));

    public const string SystemActor = "system";

    private ActivityEventAggregate Build(string action, string resourceType, Guid resourceId, string resourceName) =>
        // These are member-initiated writes — the caller always resolved RequireTenantId() before us,
        // so a tenant is guaranteed. Read it from the request context rather than threading it through.
        ActivityEventAggregate.Create(
            currentUser.RequireTenantId(), action, resourceType, resourceId, resourceName, currentUser.KeycloakUserId);
}
