namespace Titan.Application.Queries.Admin.Network.Contracts;

// Cross-tenant (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read). TenantId null = every tenant; set = narrow to one owner.
public record ListAdminNetworksQuery(Guid? TenantId);
