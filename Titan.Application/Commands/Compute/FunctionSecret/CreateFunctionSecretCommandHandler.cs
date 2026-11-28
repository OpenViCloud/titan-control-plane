using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Application.Services.Compute;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Application.Commands.Compute.FunctionSecret;

/// Titan-only: no provider command, so the request and its outcome are recorded in the same save, as an SSH key's
/// (titan-ops features/activity/design.md#actor-and-tenant). Only the ciphertext is written; the value reaches no log,
/// response or activity (titan-ops features/ovc-function/design.md#function-secrets).
public class CreateFunctionSecretCommandHandler(
    IFunctionSecretRepository repo,
    IFunctionSecretCipher cipher,
    ICurrentUser currentUser,
    IActivityRecorder activity)
{
    public async Task<CreateFunctionSecretCommandResult> HandleAsync(CreateFunctionSecretCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        ComputeInput.EnsureFunctionSecretName(command.Name);
        ComputeInput.EnsureFunctionSecretValue(command.Value);

        int count = await repo.CountByTenantAsync(tenantId, ct);
        TenantQuotaGate.EnsureFunctionSecretRoom(count);
        if (await repo.ExistsByNameAsync(tenantId, command.Name, ct))
        {
            throw new ConflictException("FUNCTION_SECRET_EXISTS", $"A function secret named '{command.Name}' already exists.");
        }

        FunctionSecretValue first = cipher.Encrypt(tenantId, command.Name, 1, command.Value);
        FunctionSecretAggregate secret = FunctionSecretAggregate.Create(tenantId, command.Name, first, currentUser.KeycloakUserId);
        repo.Add(secret);
        activity.Stage("function_secret.create_requested", "function_secret", secret.Id, secret.Name);
        activity.StageOutcome(tenantId, "function_secret.created", "function_secret", secret.Id, secret.Name);
        await repo.SaveAsync(ct);
        return new CreateFunctionSecretCommandResult(secret.LatestVersionNumber);
    }
}
