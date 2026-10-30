using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using Titan.Application.Queries.Compute.Vm.Contracts;

namespace Titan.Application.Queries.Compute.Vm;


public class GetVmQueryHandler(IVmRepository repo, ICurrentUser currentUser)
{
    public async Task<VmQueryResult> HandleAsync(GetVmQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var vm = await ComputeOwnership.RequireVmAsync(repo, tenantId, query.VmId, ct);
        return VmQueryResult.From(vm);
    }
}
