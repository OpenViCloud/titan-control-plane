using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories.Compute;
using Titan.Application.Queries.Compute.Vm.Contracts;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Queries.Compute.Vm;

public class GetVmShellTargetQueryHandler(IVmRepository repo, ICurrentUser currentUser)
{
    public async Task<VmShellTargetQueryResult> HandleAsync(GetVmShellTargetQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        Domain.Aggregates.Compute.Vm.Vm vm = await ComputeOwnership.RequireVmAsync(repo, tenantId, query.VmId, ct);
        string instanceName = vm.ShellInstanceName();
        return new VmShellTargetQueryResult(vm.ClusterId, TenantAggregate.ProjectNameFor(vm.TenantId), instanceName);
    }
}
