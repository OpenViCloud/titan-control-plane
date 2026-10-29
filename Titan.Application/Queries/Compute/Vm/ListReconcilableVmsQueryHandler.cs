using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Repositories.Compute;

namespace Titan.Application.Queries.Compute.Vm;

// Read side of the VM reconciler (titan-ops architecture/desired-and-current.md#desired-and-current). No ICurrentUser:
// the reconciler acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableVmsQueryHandler(IVmRepository vms)
{
    public async Task<List<ReconcilableVmQueryResult>> HandleAsync(ListReconcilableVmsQuery query, CancellationToken ct = default)
    {
        var rows = await vms.ListReconcilableAsync(query.ClusterId, ct);
        return rows.Select(ReconcilableVmQueryResult.From).ToList();
    }
}
