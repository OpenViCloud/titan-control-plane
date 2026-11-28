using FastEndpoints;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Function;

public class GetFunctionEndpoint(GetFunctionQueryHandler handler) : EndpointWithoutRequest<FunctionQueryResult>
{
    public override void Configure()
    {
        Get("v1/tenant/functions/{functionId:guid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new GetFunctionQuery(Route<Guid>("functionId")), ct), cancellation: ct);
    }
}
