using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Commands.Platform.Tenant.Contracts;

namespace Titan.Infrastructure.Endpoints.Tenant;

// The body of PATCH /v1/tenants/{tenantId}/debt-ceiling — the route carries the tenant (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record UpdateDebtCeilingRequest(decimal DebtCeiling);

// A reference number read at month-end close; it gates nothing
// (titan-ops architecture/money-and-wallet.md#postpaid-no-brake). Covered by TenantServiceTests.
public class UpdateTenantDebtCeilingEndpoint(UpdateTenantDebtCeilingCommandHandler updateDebtCeiling)
    : Endpoint<UpdateDebtCeilingRequest>
{
    public override void Configure()
    {
        Patch("v1/tenants/{tenantId:guid}/debt-ceiling");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(UpdateDebtCeilingRequest req, CancellationToken ct)
    {
        var tenantId = Route<Guid>("tenantId");
        await updateDebtCeiling.HandleAsync(new UpdateTenantDebtCeilingCommand(tenantId, req.DebtCeiling), ct);
        await SendNoContentAsync(ct);
    }
}

public class UpdateDebtCeilingValidator : Validator<UpdateDebtCeilingRequest>
{
    public UpdateDebtCeilingValidator()
    {
        // The domain guards this too (INVALID_DEBT_CEILING); this is the 400 that keeps a typo out of the domain.
        RuleFor(x => x.DebtCeiling)
            .GreaterThanOrEqualTo(0m)
            .WithMessage("debtCeiling must be non-negative");
    }
}
