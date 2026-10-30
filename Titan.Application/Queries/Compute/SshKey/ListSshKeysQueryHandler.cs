using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.SshKey.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.SshKey;

public class ListSshKeysQueryHandler(ISshKeyRepository repo, ICurrentUser currentUser)
{
    public async Task<List<SshKeyQueryResult>> HandleAsync(ListSshKeysQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var keys = await repo.ListByTenantAsync(tenantId, ct);
        return keys
            .Select(k => new SshKeyQueryResult(k.Id, k.Name, k.Type, k.Fingerprint, k.CreatedAt))
            .ToList();
    }
}
