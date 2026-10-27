using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

public class UpdateTemplateStatusEndpoint(UpdateTemplateStatusCommandHandler handler, IConfiguration config)
    : Endpoint<UpdateTemplateStatusRequest>
{
    public override void Configure()
    {
        Patch("internal/templates/{templateId:guid}/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdateTemplateStatusRequest req, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A report without its zone cannot say whose state it is — a worker older than per-zone templates sends
        // none; refusing it fails that run, and the next sync-all with both images live converges
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-zone).
        if (string.IsNullOrEmpty(req.ClusterId))
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        var templateId = Route<Guid>("templateId");
        await handler.HandleAsync(
            new UpdateTemplateStatusCommand(templateId, req.ClusterId, req.Report, req.Exists, req.ExternalId, req.Reason,
                req.Detail),
            ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; UpdateTemplateStatusCommand additionally carries the route id.
public record UpdateTemplateStatusRequest(
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null,
    string? ClusterId = null);       // required; nullable so a body without it reaches the 400 above
