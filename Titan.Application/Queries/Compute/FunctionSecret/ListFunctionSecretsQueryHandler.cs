using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.FunctionSecret.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.FunctionSecret;

// By name; versions newest first; uses by function name, then newest function version first. A value, or its
// ciphertext, never leaves here (titan-ops features/ovc-function/design.md#function-secrets).
public class ListFunctionSecretsQueryHandler(IFunctionSecretRepository repo, IFunctionRepository functions, ICurrentUser currentUser)
{
    public async Task<List<FunctionSecretQueryResult>> HandleAsync(ListFunctionSecretsQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var secrets = await repo.ListByTenantAsync(tenantId, ct);
        var tenantFunctions = await functions.ListByTenantAsync(tenantId, ct);
        return secrets
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new FunctionSecretQueryResult(
                s.Name,
                s.CreatedAt,
                s.LatestVersionNumber,
                s.Versions
                    .OrderByDescending(v => v.Number)
                    .Select(v => new FunctionSecretVersionQueryResult(v.Number, v.CreatedAt))
                    .ToList(),
                tenantFunctions
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .SelectMany(f => f.KeptVersionsPointingAt(s.Id)
                        .OrderByDescending(v => v.Number)
                        .SelectMany(v => v.SecretRefs
                            .Where(r => r.SecretId == s.Id)
                            .Select(r => new FunctionSecretUseQueryResult(
                                f.Id, f.Name, v.Id, v.Number, r.Version, v.Id == f.RunningVersionId))))
                    .ToList()))
            .ToList();
    }
}
