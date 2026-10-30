using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Billing.Wallet;
using Titan.Application.Commands.Billing.Wallet.Contracts;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints;

namespace Titan.Infrastructure.Auth;

// Orchestrates Platform (approve the tenant) and Billing (open its wallet) here, in the adapter layer: Platform
// depending on Billing would be a module cycle, since Billing already depends on Platform.
public class TenantResolutionMiddleware(
    ITenantRepository tenantRepository,
    BootstrapTenantCommandHandler bootstrapTenant,
    ApproveDeclaredAccountCommandHandler approveDeclaredAccount,
    OpenWalletCommandHandler openWallet) : IMiddleware
{
    public const string AutoApproveRole = "auto-approve";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var keycloakUserId = GetKeycloakUserId(context);
        if (keycloakUserId is null)
        {
            await next(context);
            return;
        }

        var result = await tenantRepository.FindTenantByMemberAsync(keycloakUserId, context.RequestAborted);

        if (!result.HasValue)
        {
            var username = context.User.FindFirst("preferred_username")?.Value
                        ?? context.User.FindFirst("email")?.Value
                        ?? keycloakUserId;
            var email = context.User.FindFirst("email")?.Value
                ?? throw new InvalidOperationException($"JWT for user {keycloakUserId} is missing required 'email' claim.");
            await bootstrapTenant.HandleAsync(
                new BootstrapTenantCommand(keycloakUserId, username, email), context.RequestAborted);
            result = await tenantRepository.FindTenantByMemberAsync(keycloakUserId, context.RequestAborted);
        }

        if (!result.HasValue)
        {
            await next(context);
            return;
        }

        // A PendingApproval tenant of an account the realm declares (role auto-approve), a new one included, is approved
        // on the spot; every other signup waits for an admin
        // (titan-ops architecture/tenancy-and-admin.md#approval-is-activation). Wallet before approval: if the process dies
        // between the two commits, the next request approves a tenant that already has a wallet; never an Active
        // tenant without one. Both steps are idempotent. Fires at most once, since approval leaves PendingApproval.
        if (result.Value.Status == TenantStatus.PendingApproval && context.User.IsInRole(AutoApproveRole))
        {
            await openWallet.HandleAsync(new OpenWalletCommand(result.Value.TenantId), context.RequestAborted);
            await approveDeclaredAccount.HandleAsync(
                new ApproveDeclaredAccountCommand(result.Value.TenantId), context.RequestAborted);
            result = await tenantRepository.FindTenantByMemberAsync(keycloakUserId, context.RequestAborted);
            if (!result.HasValue)
            {
                await next(context);
                return;
            }
        }

        context.Items["TenantId"] = result.Value.TenantId;

        // A websocket upgrade is a GET that opens a shell inside a VM (titan-ops features/vm/design.md#ssh-session): it
        // writes as much as any POST.
        bool isWrite = !HttpMethods.IsGet(context.Request.Method) || context.WebSockets.IsWebSocketRequest;

        if (result.Value.Status == TenantStatus.Suspended && isWrite)
        {
            await JsonErrorWriter.WriteAsync(context.Response, StatusCodes.Status403Forbidden, new
            {
                code = "TENANT_SUSPENDED",
                message = "Tenant is suspended. Only read operations are allowed."
            }, context.RequestAborted);
            return;
        }

        // A PendingApproval tenant is not approved yet. Reads pass (the portal shows the pending status); writes
        // are blocked until approval.
        if (result.Value.Status == TenantStatus.PendingApproval && isWrite)
        {
            await JsonErrorWriter.WriteAsync(context.Response, StatusCodes.Status403Forbidden, new
            {
                code = "TENANT_PENDING_APPROVAL",
                message = "Tenant is awaiting approval. Only read operations are allowed."
            }, context.RequestAborted);
            return;
        }

        await next(context);
    }

    private string? GetKeycloakUserId(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true) return null;
        return context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;
    }
}
