using Titan.Application.Queries.Catalog.VmTemplate.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Catalog.VmTemplate;

public class ListAdminTemplatesQueryHandler(IVmTemplateRepository repo)
{
    // Every template with its sync state, so the operator can see what is still Pending/Error after a sync.
    // Repo's ListAllAsync already orders by name. A separate admin result: TemplateQueryResult stays
    // customer-facing and keeps hiding the internal state.
    public async Task<List<AdminTemplateQueryResult>> HandleAsync(ListAdminTemplatesQuery query, CancellationToken ct = default)
    {
        var templates = await repo.ListAllAsync(ct);
        return templates.Select(AdminTemplateQueryResult.From).ToList();
    }
}
