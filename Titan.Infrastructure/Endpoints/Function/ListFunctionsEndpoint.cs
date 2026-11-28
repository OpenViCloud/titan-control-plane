using FastEndpoints;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Function;

public class ListFunctionsEndpoint(ListFunctionsQueryHandler handler) : EndpointWithoutRequest<List<FunctionQueryResult>>
{
    public override void Configure()
    {
        Get("v1/tenant/functions");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListFunctionsQuery(), ct), cancellation: ct);
    }
}
