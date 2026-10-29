using FastEndpoints;
using Titan.Application.Queries.Admin.Vm;
using Titan.Application.Queries.Admin.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read. Optional ?tenantId= narrows to one owner.
public class ListAdminVmsEndpoint(ListAdminVmsQueryHandler handler) : EndpointWithoutRequest<List<AdminVmQueryResult>>
{
    public override void Configure()
    {
        Get("v1/admin/vms");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tenantId = Query<Guid?>("tenantId", isRequired: false);
        await SendAsync(await handler.HandleAsync(new ListAdminVmsQuery(tenantId), ct), cancellation: ct);
    }
}
