using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Catalog.VmTemplate;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): each
// report of a worker that predates the request routes is mapped onto the sync the cluster is waiting on. Created and the
// give-up come from the sync command, Observed from that cluster's template reconciler, which never reports Deleted: a
// template is never removed (titan-ops features/catalog/design.md#template-reports). The template's row is locked like
// every lifecycle handler's.
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

        var template = await repo.FindByIdForUpdateAsync(req.TemplateId, ct)
            ?? throw new NotFoundException("TEMPLATE_NOT_FOUND", "Template not found.");

        PendingRequest<VmTemplateRequestKind>? waiting =
            template.Clusters.FirstOrDefault(candidate => candidate.ClusterId == req.ClusterId)?.Pending;
        switch (req.Report)
        {
            case "Observed":
                // Seeing the alias ends nothing: only a command's own report does.
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
                // With nothing waiting, the alias the command saw is recorded as an observation would.
                if (waiting != null)
                {
                    template.EndSyncRequest(req.ClusterId, waiting.Id, VmTemplateRequestEnd.Done(req.ExternalId));
                }
                else
                {
                    template.MarkAliasObservedIn(req.ClusterId, req.ExternalId!);
                }
                break;
            case "RequestFailed":
                // With nothing waiting the give-up is the stale report of a sync already over, and records nothing.
                if (waiting != null)
                {
                    template.EndSyncRequest(req.ClusterId, waiting.Id,
                        VmTemplateRequestEnd.Failed(req.ExternalId, req.Reason, req.Detail));
                }
                break;
        }

        await repo.SaveAsync(ct);
        logger.LogInformation("Template {TemplateId} in cluster {ClusterId}: report {Report} exists={Exists}",
            req.TemplateId, req.ClusterId, req.Report, req.Exists);
    }
}
