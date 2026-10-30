using ActivityEventAggregate = Titan.Domain.Aggregates.ActivityEvent.ActivityEvent;

namespace Titan.Application.Queries.Platform.ActivityEvent.Contracts;

public record ActivityEventQueryResult(
    Guid Id,
    string Action,
    string ResourceType,
    string ResourceName,
    DateTimeOffset CreatedAt)
{
    public static ActivityEventQueryResult From(ActivityEventAggregate e) =>
        new(e.Id, e.Action, e.ResourceType, e.ResourceName, e.CreatedAt);
}
