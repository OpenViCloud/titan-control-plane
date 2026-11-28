using FastEndpoints;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Function;

public class ListFunctionVersionsEndpoint(ListFunctionVersionsQueryHandler handler)
    : EndpointWithoutRequest<List<FunctionVersionQueryResult>>
{
    public override void Configure()
    {
        Get("v1/tenant/functions/{functionId:guid}/versions");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListFunctionVersionsQuery(Route<Guid>("functionId")), ct), cancellation: ct);
    }
}
