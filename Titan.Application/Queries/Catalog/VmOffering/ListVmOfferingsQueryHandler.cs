using Titan.Application.Queries.Catalog.VmOffering.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Catalog.VmOffering;

public class ListVmOfferingsQueryHandler(IVmOfferingRepository repo)
{
    public async Task<List<VmOfferingQueryResult>> HandleAsync(ListVmOfferingsQuery query, CancellationToken ct = default)
    {
        var offerings = await repo.ListAllAsync(ct);
        // Smallest-first so the portal dropdown reads s → m → l → xl. Sorting by Id would give
        // l, m, s, xl. (VcpuCount, MemoryMb) keeps tiers with equal vCPU deterministically ordered.
        return offerings
            .OrderBy(o => o.VcpuCount).ThenBy(o => o.MemoryMb)
            .Select(VmOfferingQueryResult.From)
            .ToList();
    }
}
