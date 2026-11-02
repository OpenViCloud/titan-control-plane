using FastEndpoints;
using Titan.Application.Queries.Admin.Request;
using Titan.Application.Queries.Admin.Request.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// titan-ops architecture/resource-lifecycle.md#lost-and-stale: the requests waiting past the threshold, with the status of
// the command row each one names. Platform-admin only, outside the public contract like every v1/admin route.
public class ListStuckRequestsEndpoint(ListStuckRequestsQueryHandler handler)
    : EndpointWithoutRequest<List<StuckRequestQueryResult>>
{
    public override void Configure()
    {
        Get("v1/admin/requests/stuck");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListStuckRequestsQuery(), ct), cancellation: ct);
    }
}
