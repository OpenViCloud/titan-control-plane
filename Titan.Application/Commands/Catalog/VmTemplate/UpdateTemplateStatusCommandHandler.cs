using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Catalog;

namespace Titan.Application.Commands.Catalog.VmTemplate;

// Worker callback — an observation of one cluster (titan-ops architecture/intent-and-observation.md#observation-converges).
// Created and the give-up come from the sync command, Observed from that cluster's template reconciler, which never
// reports Deleted: a template is never removed (titan-ops features/catalog/design.md#template-reports).
public class UpdateTemplateStatusCommandHandler(
    IVmTemplateRepository repo,
    ClusterOptions clusters,
    ILogger<UpdateTemplateStatusCommandHandler> logger)
{
    public async Task HandleAsync(UpdateTemplateStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before anything is loaded: a malformed report is the worker's bug, whatever the template.
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(req.ExternalId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalId is required when report is Created.");
                }
                break;
            case "RequestFailed":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown template report: '{req.Report}'");
        }

        // The aggregate records an observation for any cluster it is handed; whether the cluster exists is checked here.
        clusters.ClusterOf(req.ClusterId);

        var template = await repo.FindByIdAsync(req.TemplateId, ct)
            ?? throw new NotFoundException("TEMPLATE_NOT_FOUND", "Template not found.");

        switch (req.Report)
        {
            case "Observed":
                bool aliasIsThere = req.Exists == true;
                if (aliasIsThere)
                {
                    template.MarkObservedIn(req.ClusterId);
                }
                else
                {
                    template.MarkNotFoundIn(req.ClusterId);
                }
                break;
            case "Created":
                template.MarkCreatedIn(req.ClusterId, req.ExternalId!);
                break;
            case "RequestFailed":
                template.MarkRequestFailedIn(req.ClusterId, req.Reason, req.Detail);
                break;
        }

        await repo.SaveAsync(ct);
        logger.LogInformation("Template {TemplateId} in cluster {ClusterId}: report {Report} exists={Exists}",
            req.TemplateId, req.ClusterId, req.Report, req.Exists);
    }
}
