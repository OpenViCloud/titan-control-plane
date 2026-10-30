using Titan.Application.Queries.Admin.Vm.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Admin.Vm;

// Cross-tenant (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read) VM read for platform-admin. Straight to the repository, never
// through a tenant-scoped handler (which would require a caller tenant).
public class ListAdminVmsQueryHandler(IVmRepository vms)
{
    public async Task<List<AdminVmQueryResult>> HandleAsync(ListAdminVmsQuery query, CancellationToken ct = default) =>
        (await vms.ListAllAsync(query.TenantId, ct)).Select(AdminVmQueryResult.From).ToList();
}
