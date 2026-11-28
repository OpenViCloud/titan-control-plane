using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Catalog.FunctionRuntime;
using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

// 202: the function worker pulls later, so the admin reads the outcome back from GET /v1/admin/function-runtimes
// (titan-ops architecture/http-contract.md#async-actions-return-202).
public class SyncFunctionRuntimesEndpoint(SyncFunctionRuntimesCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("v1/admin/function-runtimes/sync");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await handler.HandleAsync(new SyncFunctionRuntimesCommand(), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted));
    }
}
