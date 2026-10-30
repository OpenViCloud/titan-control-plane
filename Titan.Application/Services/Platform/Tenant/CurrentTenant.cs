using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Services.Platform.Tenant;

/// <summary>
/// The caller's own tenant, shared by GetTenant and ListTenantMembers: handlers may not call handlers
/// (ReadWriteSeparationTests.Handlers_DoNotReferenceOtherHandlers). One copy keeps NOT_A_MEMBER and
/// TENANT_NOT_FOUND from drifting apart between the two paths.
/// </summary>
public static class CurrentTenant
{
    public static async Task<TenantAggregate> GetAsync(ITenantRepository repo, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.TenantId is null)
            throw new ForbiddenException("NOT_A_MEMBER", "You do not belong to any tenant.");

        return await repo.FindByIdAsync(currentUser.TenantId.Value, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
    }
}
