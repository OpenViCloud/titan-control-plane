using FastEndpoints;
using Titan.Application.Queries.Platform.ActivityEvent;
using Titan.Application.Queries.Platform.ActivityEvent.Contracts;

namespace Titan.Infrastructure.Endpoints.Activity;

// titan-ops features/activity/design.md#activity-data-and-route
public class GetActivityEndpoint(ListActivityEventsQueryHandler listActivity) : EndpointWithoutRequest<List<ActivityEventQueryResult>>
{
    public override void Configure()
    {
        Get("v1/tenant/activity");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Absent/invalid limit → 0, which the handler maps to its default; over-cap is clamped there.
        var limit = Query<int>("limit", isRequired: false);
        await SendOkAsync(await listActivity.HandleAsync(new ListActivityEventsQuery(limit), ct), ct);
    }
}
