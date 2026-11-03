using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a template sync's command in one cluster ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes); a template's state lives per cluster, so the route sits
// under the cluster. 204 also when the cluster is not waiting on that request.
public class EndTemplateSyncRequestEndpoint(EndTemplateSyncRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndTemplateSyncRequestBody>
{
    public override void Configure()
    {
        Patch("internal/templates/{templateId:guid}/clusters/{clusterId}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndTemplateSyncRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await handler.HandleAsync(new EndTemplateSyncRequestCommand(
            Route<Guid>("templateId"), Route<string>("clusterId")!, Route<Guid>("requestId"),
            body.Outcome, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the three route values. ExternalId is the image alias.
public record EndTemplateSyncRequestBody(
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
