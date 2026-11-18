using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Application.Queries.Compute.VmKeyAddition.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.VmKeyAddition;

public class GetVmKeyAdditionQueryHandler(
    IVmRepository vmRepo,
    IVmKeyAdditionRepository additionRepo,
    ICurrentUser currentUser)
{
    public async Task<VmKeyAdditionQueryResult> HandleAsync(GetVmKeyAdditionQuery query, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        await ComputeOwnership.RequireVmAsync(vmRepo, callerId, query.VmId, ct);

        var addition = await additionRepo.FindByIdAsync(query.KeyAdditionId, ct);
        if (addition == null || addition.VmId != query.VmId)
        {
            throw new NotFoundException("VM_KEY_ADDITION_NOT_FOUND", "Key addition not found.");
        }
        return VmKeyAdditionQueryResult.From(addition);
    }
}
