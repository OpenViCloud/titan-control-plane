using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The template reconciler's observation of one cluster (titan-ops architecture/resource-lifecycle.md#callback-routes):
// whether it holds the alias. Ends no request.
public class ObserveTemplateEndpoint(ObserveTemplateCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveTemplateBody>
{
    public override void Configure()
    {
        Patch("internal/templates/{templateId:guid}/clusters/{clusterId}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveTemplateBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A body without exists would read as "gone" and record the resource missing; nothing is written.
        if (body.Exists == null)
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        await handler.HandleAsync(
            new ObserveTemplateCommand(Route<Guid>("templateId"), Route<string>("clusterId")!, body.Exists.Value), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the two route values.
// Exists is required; nullable so a body without it reaches the 400 above.
public record ObserveTemplateBody(bool? Exists);
