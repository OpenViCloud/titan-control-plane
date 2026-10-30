using Titan.Application.Abstractions;
using Titan.Application.Queries.Billing.TopUp.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.TopUp;

/// The caller tenant's own top-ups, newest first (tenant list / receipts).
public class ListMyTopUpsQueryHandler(ICreditRepository repo, ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<TopUpQueryResult>> HandleAsync(ListMyTopUpsQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var list = await repo.ListTopUpsByTenantAsync(tenantId, ct);
        return list.Select(TopUpQueryResult.From).ToList();
    }
}
