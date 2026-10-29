using Titan.Domain.Exceptions;

namespace Titan.Application.Abstractions;

public static class CurrentUserExtensions
{
    /// <summary>
    /// The caller's tenant, or throws 403 NOT_A_MEMBER if they belong to no tenant.
    /// Single source of truth for tenant-scoping authorization (titan-ops architecture/http-contract.md#tenant-scoping Style B).
    /// </summary>
    public static Guid RequireTenantId(this ICurrentUser currentUser) =>
        currentUser.TenantId ?? throw new ForbiddenException("NOT_A_MEMBER", "You do not belong to any tenant.");
}
