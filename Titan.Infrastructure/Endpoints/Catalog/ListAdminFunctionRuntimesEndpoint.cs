using FastEndpoints;
using Titan.Application.Queries.Catalog.FunctionRuntime;
using Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

// Every runtime with the sync of each function cluster, for the operator.
public class ListAdminFunctionRuntimesEndpoint(ListAdminFunctionRuntimesQueryHandler handler)
    : EndpointWithoutRequest<List<AdminFunctionRuntimeQueryResult>>
{
    public override void Configure()
    {
        Get("v1/admin/function-runtimes");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListAdminFunctionRuntimesQuery(), ct), cancellation: ct);
    }
}
