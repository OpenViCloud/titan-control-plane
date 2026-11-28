using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Application.Commands.Compute.FunctionSecret;

/// A hard delete of the secret and every version (titan-ops features/ovc-function/design.md#function-secrets), refused
/// while the running or deploying version of a function not Deleted points at it: that version could not be deployed
/// again. An older function version that points at it is left unable to deploy (422 FUNCTION_SECRET_NOT_FOUND). A
/// repeat finds nothing and is 404, like a name that never existed.
public class DeleteFunctionSecretCommandHandler(
    IFunctionSecretRepository repo,
    IFunctionRepository functions,
    ICurrentUser currentUser,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteFunctionSecretCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        FunctionSecretAggregate secret = await ComputeOwnership.RequireFunctionSecretForUpdateAsync(repo, tenantId, command.Name, ct);

        List<string> users = (await functions.ListByTenantAsync(tenantId, ct))
            .Where(f => f.RunsOrDeploysWithSecret(secret.Id))
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (users.Count > 0)
        {
            throw new ConflictException("FUNCTION_SECRET_IN_USE",
                $"Function secret '{secret.Name}' is used by the running or deploying version of: {string.Join(", ", users)}.");
        }

        repo.Remove(secret);
        activity.Stage("function_secret.delete_requested", "function_secret", secret.Id, secret.Name);
        activity.StageOutcome(tenantId, "function_secret.deleted", "function_secret", secret.Id, secret.Name);
        await repo.SaveAsync(ct);
    }
}
