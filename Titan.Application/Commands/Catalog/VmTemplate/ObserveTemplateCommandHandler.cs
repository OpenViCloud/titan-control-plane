using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;

namespace Titan.Application.Commands.Catalog.VmTemplate;

// The template reconciler's observation of one cluster (titan-ops architecture/resource-lifecycle.md#callback-routes):
// current only, no sync ends here. Authenticated by X-Internal-Token at the endpoint; the template's row is locked like
// every lifecycle handler's.
public class ObserveTemplateCommandHandler(IVmTemplateRepository repo, ClusterOptions clusters)
{
    public async Task HandleAsync(ObserveTemplateCommand command, CancellationToken ct = default)
    {
        // The aggregate records an observation for any cluster it is handed; whether the cluster exists is checked here.
        clusters.ClusterOf(command.ClusterId);

        VmTemplateAggregate template = await repo.FindByIdForUpdateAsync(command.TemplateId, ct)
            ?? throw new NotFoundException("TEMPLATE_NOT_FOUND", "Template not found.");

        if (command.Exists)
        {
            template.MarkObservedIn(command.ClusterId);
        }
        else
        {
            template.MarkNotFoundIn(command.ClusterId);
        }

        await repo.SaveAsync(ct);
    }
}
