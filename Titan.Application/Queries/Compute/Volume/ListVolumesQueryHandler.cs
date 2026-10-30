using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Volume.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.Volume;

public class ListVolumesQueryHandler(IVolumeRepository repo, ICurrentUser currentUser)
{
    public async Task<List<VolumeQueryResult>> HandleAsync(ListVolumesQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var volumes = await repo.ListByTenantAsync(tenantId, ct);
        return volumes.Select(VolumeQueryResult.From).ToList();
    }
}
