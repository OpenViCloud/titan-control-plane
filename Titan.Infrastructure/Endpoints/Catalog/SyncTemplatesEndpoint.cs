using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

// 202: the worker reconciles later, so the admin reads the outcome back from GET /v1/admin/templates
// (titan-ops architecture/http-contract.md#async-actions-return-202).
public class SyncTemplatesEndpoint(SyncTemplatesCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("v1/admin/templates/sync");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await handler.HandleAsync(new SyncTemplatesCommand(), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted));
    }
}
