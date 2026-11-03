namespace Titan.Application.Queries.Admin.Request.Contracts;

// Platform-admin only (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read): every tenant's stuck requests, and
// the stuck template syncs, which belong to no tenant.
// The threshold comes from RequestStuckOptions, not from the caller, so the list and the health check agree.
public record ListStuckRequestsQuery;
