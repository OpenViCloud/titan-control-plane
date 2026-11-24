using FastEndpoints;
using Titan.Application.Commands.Admin.Vm;
using Titan.Application.Commands.Admin.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// An admin write on a tenant's resources (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write); the
// others are the two restores of a backup archive, beside this file.
public class DeleteAdminVmEndpoint(DeleteAdminVmCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/admin/vms/{vmId:guid}");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteAdminVmCommand(Route<Guid>("vmId")), ct);
        await SendNoContentAsync(ct);
    }
}
