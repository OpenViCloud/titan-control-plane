using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Catalog.VmTemplate;

// The worker's report that a template sync's command in one cluster ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes). Authenticated by X-Internal-Token at the endpoint. The
// template's row is locked so a sync-all meanwhile is ordered before or after this report, never interleaved
// (titan-ops architecture/resource-lifecycle.md#one-key-per-request). A report the cluster is not waiting on is ignored
// and still answers 204: the worker has nothing to retry.
public class EndTemplateSyncRequestCommandHandler(IVmTemplateRepository repo, ClusterOptions clusters)
{
    public async Task HandleAsync(EndTemplateSyncRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before anything is loaded: a malformed report is the worker's bug, whatever the template.
        RequestOutcome outcome = ParseOutcome(command.Outcome);

        // The aggregate records a report for any cluster it is handed; whether the cluster exists is checked here.
        clusters.ClusterOf(command.ClusterId);

        VmTemplateAggregate template = await repo.FindByIdForUpdateAsync(command.TemplateId, ct)
            ?? throw new NotFoundException("TEMPLATE_NOT_FOUND", "Template not found.");

        VmTemplateRequestEnd end = new(outcome, command.ExternalId, command.Reason, command.Detail);
        PendingRequest<VmTemplateRequestKind>? ended = template.EndSyncRequest(command.ClusterId, command.RequestId, end);
        if (ended == null)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }

    private static RequestOutcome ParseOutcome(string? outcome)
    {
        switch (outcome)
        {
            case "Done":
                return RequestOutcome.Done;
            case "Failed":
                return RequestOutcome.Failed;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown request outcome: '{outcome}'");
        }
    }
}
