using FastEndpoints;
using Titan.Application.Commands.Billing.TenantActivation;
using Titan.Application.Commands.Billing.TenantActivation.Contracts;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Tenant;

namespace Titan.Infrastructure.Endpoints.Tenant;

public record UpdateStatusRequest(string Status);

// One route, two modules: Active is Billing's handler, which opens the wallet with the activation
// (titan-ops architecture/tenancy-and-admin.md#approval-is-activation); Suspended and anything else is Platform's. The
// status is read before the tenant is looked up, so a status that is no status name answers 400 before a 404.
public class UpdateTenantStatusEndpoint(
    UpdateTenantStatusCommandHandler updateStatus,
    ActivateTenantCommandHandler activateTenant) : Endpoint<UpdateStatusRequest>
{
    public override void Configure()
    {
        Patch("v1/tenants/{tenantId:guid}/status");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(UpdateStatusRequest req, CancellationToken ct)
    {
        var tenantId = Route<Guid>("tenantId");

        TenantStatus target = UpdateTenantStatusCommandHandler.ParseTarget(req.Status);
        if (target == TenantStatus.Active)
        {
            await activateTenant.HandleAsync(new ActivateTenantCommand(tenantId), ct);
        }
        else
        {
            await updateStatus.HandleAsync(new UpdateTenantStatusCommand(tenantId, target), ct);
        }
        await SendNoContentAsync(ct);
    }
}
