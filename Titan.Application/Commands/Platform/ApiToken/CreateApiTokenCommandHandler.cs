using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.ApiToken.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;
using ApiTokenAggregate = Titan.Domain.Aggregates.Platform.ApiToken.ApiToken;

namespace Titan.Application.Commands.Platform.ApiToken;

/// Titan-only: no provider command, so the request and its outcome are recorded in the same save
/// (titan-ops features/activity/design.md#actor-and-tenant).
public class CreateApiTokenCommandHandler(
    IApiTokenRepository repo,
    ICurrentUser currentUser,
    IActivityRecorder activity,
    TimeProvider clock)
{
    public async Task<CreateApiTokenCommandResult> HandleAsync(CreateApiTokenCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        int tokenCount = await repo.CountByTenantAsync(tenantId, ct);

        ApiTokenAggregate token = ApiTokenAggregate.Create(
            tenantId, currentUser.KeycloakUserId, tokenCount, command.Name, command.Token, command.ExpiresInDays,
            clock.GetUtcNow());

        bool nameTaken = await repo.ExistsByNameAsync(tenantId, token.Name, ct);
        if (nameTaken)
        {
            throw new ConflictException("API_TOKEN_NAME_TAKEN", $"An API token named '{token.Name}' already exists.");
        }

        // Only a client that sends the same secret twice under two Idempotency-Keys gets here.
        bool secretTaken = await repo.ExistsBySecretHashAsync(token.SecretHash, ct);
        if (secretTaken)
        {
            throw new ConflictException("API_TOKEN_EXISTS", "This token is already registered; generate a new one.");
        }

        repo.Add(token);
        activity.Stage("api_token.create_requested", "api_token", token.Id, token.Name);
        activity.StageOutcome(tenantId, "api_token.created", "api_token", token.Id, token.Name);
        await repo.SaveAsync(ct);

        return new CreateApiTokenCommandResult(token.Id);
    }
}
