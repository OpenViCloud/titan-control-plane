using Titan.Application.Options;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;
using Titan.Domain.Repositories.Catalog;

namespace Titan.Application.Queries.Catalog.VmTemplate;

// A template with no row for a configured cluster was never asked into it (a new environment, a cluster added to the
// config, a template added by a migration); a pair asked once has its row, even when that sync failed
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-sync-never-asked).
public class ListClustersWithNeverAskedTemplatesQueryHandler(IVmTemplateRepository repo, ClusterOptions clusters)
{
    public async Task<List<string>> HandleAsync(ListClustersWithNeverAskedTemplatesQuery query, CancellationToken ct = default)
    {
        var templates = await repo.ListAllAsync(ct);
        return clusters.Clusters
            .Select(c => c.Id)
            .Where(clusterId => templates.Any(t => t.ClusterOrNull(clusterId) == null))
            .ToList();
    }
}
