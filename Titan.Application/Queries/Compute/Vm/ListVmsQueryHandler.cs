using Titan.Application.Abstractions;
using Titan.Domain.Repositories.Compute;
using Titan.Application.Queries.Compute.Vm.Contracts;

namespace Titan.Application.Queries.Compute.Vm;


// Exactly two dependencies (ReadWriteSeparationTests.ListVmsQueryHandler_TakesExactlyItsTwoDependencies):
// no IProviderCommandQueue and no IActivityRecorder, so this path cannot enqueue a provider command or write an activity row
// (titan-ops architecture/code-structure.md#query-handlers-cannot-write).
public class ListVmsQueryHandler(IVmRepository repo, ICurrentUser currentUser)
{
    public async Task<List<VmQueryResult>> HandleAsync(ListVmsQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var vms = await repo.ListByTenantAsync(tenantId, ct);
        return vms.Select(VmQueryResult.From).ToList();
    }
}
