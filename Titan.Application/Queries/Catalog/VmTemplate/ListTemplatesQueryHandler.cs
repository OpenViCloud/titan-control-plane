using Titan.Application.Options;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;
using Titan.Domain.Repositories;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;

namespace Titan.Application.Queries.Catalog.VmTemplate;

public class ListTemplatesQueryHandler(IVmTemplateRepository repo, ClusterOptions clusters)
{
    // Customer read — only templates whose alias exists in the cluster, ordered by name. Repo owns the filter; the handler owns the ordering so the
    // contract is testable at service level. A cluster that does not exist is refused like it is at create
    // (titan-ops features/catalog/design.md#cluster-is-a-real-choice), not answered with an empty list that reads as
    // "nothing synced there".
    public async Task<List<TemplateQueryResult>> HandleAsync(ListTemplatesQuery query, CancellationToken ct = default)
    {
        List<VmTemplateAggregate> templates;
        string? clusterId = query.ClusterId;
        if (string.IsNullOrEmpty(clusterId))
        {
            templates = await repo.ListSyncedInAnyClusterAsync(ct);
        }
        else
        {
            clusters.ClusterOf(clusterId);
            templates = await repo.ListSyncedInClusterAsync(clusterId, ct);
        }
        // Not listed ⇒ not offered, synced or not (titan-ops features/catalog/design.md#template-listed).
        return templates
            .Where(t => t.IsListed)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(TemplateQueryResult.From)
            .ToList();
    }
}
