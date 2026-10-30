using Titan.Application.Queries.Catalog.VolumeOffering.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Catalog.VolumeOffering;

public class ListVolumeOfferingsQueryHandler(IVolumeOfferingRepository repo)
{
    public async Task<List<VolumeOfferingQueryResult>> HandleAsync(ListVolumeOfferingsQuery query, CancellationToken ct = default)
    {
        var offerings = await repo.ListAllAsync(ct);
        return offerings
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(VolumeOfferingQueryResult.From)
            .ToList();
    }
}
