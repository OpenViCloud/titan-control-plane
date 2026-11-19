using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

/// The admin's sync-all (SyncTemplatesCommandHandler) for the restore Job, which rebuilds a cluster on a host that holds
/// none of the template images; it reports each alias NotFound first, so the sync sends to every one
/// (titan-ops features/backup/design.md#cluster-restore). 202: the worker syncs later.
public class SyncTemplatesInternalEndpoint(SyncTemplatesCommandHandler handler, IConfiguration config) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("internal/templates/sync");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }
        await handler.HandleAsync(new SyncTemplatesCommand(), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted));
    }
}
