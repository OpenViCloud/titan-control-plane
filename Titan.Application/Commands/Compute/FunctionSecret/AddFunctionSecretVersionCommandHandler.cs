using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Application.Commands.Compute.FunctionSecret;

/// A new value is a new version (titan-ops features/ovc-function/design.md#function-secrets): no function changes until a
/// new function version points at it and is deployed. The secret's row is locked before the number is chosen, so two new
/// values take turns and the second is numbered after the first; the old versions a kept function version points at are
/// kept past KeptVersions.
public class AddFunctionSecretVersionCommandHandler(
    IFunctionSecretRepository repo,
    IFunctionRepository functions,
    IFunctionSecretCipher cipher,
    ICurrentUser currentUser,
    IActivityRecorder activity)
{
    public async Task<AddFunctionSecretVersionCommandResult> HandleAsync(AddFunctionSecretVersionCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        ComputeInput.EnsureFunctionSecretValue(command.Value);
        FunctionSecretAggregate secret = await ComputeOwnership.RequireFunctionSecretForUpdateAsync(repo, tenantId, command.Name, ct);

        List<FunctionAggregate> tenantFunctions = await functions.ListByTenantAsync(tenantId, ct);
        HashSet<int> pinned = tenantFunctions
            .SelectMany(f => f.KeptVersionsPointingAt(secret.Id))
            .SelectMany(v => v.SecretRefs.Where(r => r.SecretId == secret.Id).Select(r => r.Version))
            .ToHashSet();

        int number = secret.NextVersionNumber;
        FunctionSecretValue value = cipher.Encrypt(tenantId, secret.Name, number, command.Value);
        FunctionSecretVersion version = secret.AddVersion(value, currentUser.KeycloakUserId, pinned);
        activity.Stage("function_secret.version_add_requested", "function_secret", secret.Id, secret.Name);
        activity.StageOutcome(tenantId, "function_secret.version_added", "function_secret", secret.Id, secret.Name);
        await repo.SaveAsync(ct);
        return new AddFunctionSecretVersionCommandResult(version.Number);
    }
}
