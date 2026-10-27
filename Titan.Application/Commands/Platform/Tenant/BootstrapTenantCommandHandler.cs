using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Platform.Tenant;

/// Creates the caller's tenant on first sign-in. Idempotent: a caller who already owns a tenant is a no-op.
/// Every signup starts PendingApproval; the middleware approves one of an account the realm declares in the same request, after
/// opening its wallet (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#approval-is-activation).
public class BootstrapTenantCommandHandler(ITenantRepository repo)
{
    public async Task HandleAsync(BootstrapTenantCommand command, CancellationToken ct = default)
    {
        string keycloakUserId = command.KeycloakUserId;

        // Repeat (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat): the middleware calls this on a
        // signed-in caller's requests; the first one created the tenant, every later one finds it.
        bool alreadyBootstrapped = await repo.ExistsByOwnerAsync(keycloakUserId, ct);
        if (alreadyBootstrapped)
        {
            return;
        }

        TenantAggregate tenant = TenantAggregate.CreatePending(command.Username, keycloakUserId, command.Email);
        repo.Add(tenant);
        try
        {
            await repo.SaveAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Tenant and owner commit together, so a lost insert either means the caller's own other first request
            // won (it owns a tenant now) or the name is another user's
            // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#tenant-name-taken).
            bool ownRequestWon = await repo.ExistsByOwnerAsync(keycloakUserId, ct);
            if (ownRequestWon)
            {
                return;
            }
            throw new ConflictException("TENANT_NAME_TAKEN", $"The tenant name '{command.Username}' belongs to another account.");
        }
    }
}
