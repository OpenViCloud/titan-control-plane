using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.ApiToken.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using ApiTokenAggregate = Titan.Domain.Aggregates.ApiToken.ApiToken;

namespace Titan.Application.Commands.Platform.ApiToken;

/// Revoking deletes the row, so the next request with the token is 401. A repeat finds nothing and is 404, like
/// an id that never existed.
public class DeleteApiTokenCommandHandler(IApiTokenRepository repo, ICurrentUser currentUser, IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteApiTokenCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        ApiTokenAggregate? token = await repo.FindByIdAsync(command.ApiTokenId, ct);
        if (token == null || token.TenantId != tenantId)
        {
            throw new NotFoundException("API_TOKEN_NOT_FOUND", $"API token '{command.ApiTokenId}' not found.");
        }

        repo.Remove(token);
        activity.Stage("api_token.delete_requested", "api_token", token.Id, token.Name);
        activity.StageOutcome(tenantId, "api_token.deleted", "api_token", token.Id, token.Name);
        await repo.SaveAsync(ct);
    }
}
