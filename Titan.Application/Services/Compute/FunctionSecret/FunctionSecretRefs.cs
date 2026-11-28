using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Application.Services.Compute.FunctionSecret;

/// The secrets a function version pins, read where the version is used: its deploy and the worker's read of the values
/// (titan-ops features/ovc-function/design.md#function-secrets). A secret deleted since the upload, or a version of it
/// no longer kept, is 422 FUNCTION_SECRET_NOT_FOUND: the function version still names it, the secret does not answer.
public static class FunctionSecretRefs
{
    /// Each ref with the secret version it names, in the version's order.
    public static async Task<List<(FunctionSecretRef Ref, FunctionSecretAggregate Secret, FunctionSecretVersion Version)>> RequireKeptAsync(
        IFunctionSecretRepository repo, Guid tenantId, IReadOnlyCollection<FunctionSecretRef> refs, CancellationToken ct)
    {
        var found = new List<(FunctionSecretRef, FunctionSecretAggregate, FunctionSecretVersion)>();
        if (refs.Count == 0)
        {
            return found;
        }
        List<Guid> ids = refs.Select(r => r.SecretId).Distinct().ToList();
        Dictionary<Guid, FunctionSecretAggregate> secrets = (await repo.ListByIdsAsync(tenantId, ids, ct)).ToDictionary(s => s.Id);
        foreach (FunctionSecretRef secretRef in refs)
        {
            FunctionSecretVersion? version = null;
            if (secrets.TryGetValue(secretRef.SecretId, out FunctionSecretAggregate? secret))
            {
                version = secret.FindVersion(secretRef.Version);
            }
            if (secret == null || version == null)
            {
                throw new BusinessException("FUNCTION_SECRET_NOT_FOUND",
                    $"Secret '{secretRef.SecretName}' version {secretRef.Version}, which {secretRef.EnvName} reads, no longer exists.");
            }
            found.Add((secretRef, secret, version));
        }
        return found;
    }
}
