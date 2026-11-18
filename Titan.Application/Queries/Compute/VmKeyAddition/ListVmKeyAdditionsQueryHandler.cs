using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Application.Queries.Compute.VmKeyAddition.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.VmKeyAddition;

public class ListVmKeyAdditionsQueryHandler(
    IVmRepository vmRepo,
    IVmKeyAdditionRepository additionRepo,
    ICurrentUser currentUser)
{
    public async Task<List<VmKeyAdditionQueryResult>> HandleAsync(ListVmKeyAdditionsQuery query, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        await ComputeOwnership.RequireVmAsync(vmRepo, callerId, query.VmId, ct);

        var additions = await additionRepo.FindByVmIdAsync(query.VmId, ct);
        return additions.Select(VmKeyAdditionQueryResult.From).ToList();
    }
}
