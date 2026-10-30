using Titan.Application.Queries.Billing.TopUp.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.TopUp;

/// Admin TopUp history, newest first, across every tenant — no ICurrentUser: platform-admin only.
public class ListTopUpsQueryHandler(ICreditRepository repo)
{
    public async Task<IReadOnlyList<TopUpQueryResult>> HandleAsync(ListTopUpsQuery query, CancellationToken ct = default)
    {
        var list = await repo.ListTopUpsAsync(query.Source, ct);
        return list.Select(TopUpQueryResult.From).ToList();
    }
}
