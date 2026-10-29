using FastEndpoints;
using Titan.Application.Commands.Billing.Wallet;
using Titan.Application.Commands.Billing.Wallet.Contracts;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Repositories.Platform;

namespace Titan.Infrastructure.Endpoints.Tenant;

public record UpdateStatusRequest(string Status);

// Orchestrates Platform (activate the tenant) and Billing (open its wallet) here, in the adapter layer: Platform
// depending on Billing would be a module cycle, since Billing already depends on Platform.
public class UpdateTenantStatusEndpoint(
    UpdateTenantStatusCommandHandler updateStatus,
    OpenWalletCommandHandler openWallet,
    ITenantRepository tenants) : Endpoint<UpdateStatusRequest>
{
    public override void Configure()
    {
        Patch("v1/tenants/{tenantId:guid}/status");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(UpdateStatusRequest req, CancellationToken ct)
    {
        var tenantId = Route<Guid>("tenantId");

        // Wallet before activation (titan-ops architecture/tenancy-and-admin.md#approval-is-activation): if the process dies
        // between the two commits, a repeat activates a tenant that already has one. Only for a tenant that exists,
        // since nothing ties wallets to tenants in the database and an orphan wallet would stay behind.
        bool asksForActive = UpdateTenantStatusCommandHandler.ParseTarget(req.Status) == TenantStatus.Active;
        if (asksForActive)
        {
            bool tenantExists = await tenants.FindByIdAsync(tenantId, ct) is not null;
            if (tenantExists)
            {
                await openWallet.HandleAsync(new OpenWalletCommand(tenantId), ct);
            }
        }

        await updateStatus.HandleAsync(new UpdateTenantStatusCommand(tenantId, req.Status), ct);
        await SendNoContentAsync(ct);
    }
}
