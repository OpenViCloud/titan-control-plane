using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Application.Services.Compute.FunctionSecret;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Queries.Compute.Function;

// The worker's read of the values a version pins (titan-ops features/ovc-function/design.md#function-secrets): the one
// place a value is decrypted. Internal: no tenant context, the function's own tenant scopes the secrets. A function or
// version it does not know, or a deleted function, is 404; a secret or secret version gone since the upload is 422 FUNCTION_SECRET_NOT_FOUND.
public class GetFunctionSecretValuesQueryHandler(
    IFunctionRepository repo,
    IFunctionSecretRepository secrets,
    IFunctionSecretCipher cipher)
{
    public async Task<FunctionSecretValuesQueryResult> HandleAsync(GetFunctionSecretValuesQuery query, CancellationToken ct = default)
    {
        // A deleted function is deployed no more: its secrets are as unknown as it is.
        FunctionAggregate? function = await repo.FindByIdAsync(query.FunctionId, ct);
        if (function == null || function.IsDeleted)
        {
            throw new NotFoundException("FUNCTION_NOT_FOUND", "Function not found.");
        }
        FunctionVersion version = function.GetVersion(query.VersionId);

        var found = await FunctionSecretRefs.RequireKeptAsync(secrets, function.TenantId, version.SecretRefs, ct);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (secretRef, secret, secretVersion) in found)
        {
            values[secretRef.EnvName] = cipher.Decrypt(function.TenantId, secret.Name, secretVersion.Number,
                secretVersion.KeyId, secretVersion.Nonce, secretVersion.Ciphertext);
        }
        return new FunctionSecretValuesQueryResult(values);
    }
}
