using FastEndpoints;
using Titan.Application.Queries.Platform.AdminAuditLog;
using Titan.Application.Queries.Platform.AdminAuditLog.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// titan-ops architecture/tenancy-and-admin.md#admin-audit. ?limit= is clamped in the handler; covered by AuditLogServiceTests.
public class ListAuditLogEndpoint(ListAdminAuditLogQueryHandler listAuditLog) : EndpointWithoutRequest<List<AdminAuditLogQueryResult>>
{
    public override void Configure()
    {
        Get("v1/admin/audit-log");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var limit = Query<int>("limit", isRequired: false);
        await SendAsync(await listAuditLog.HandleAsync(new ListAdminAuditLogQuery(limit), ct), cancellation: ct);
    }
}
