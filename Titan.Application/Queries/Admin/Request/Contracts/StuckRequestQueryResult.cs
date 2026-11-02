using Titan.Application.Abstractions;

namespace Titan.Application.Queries.Admin.Request.Contracts;

// One request waiting past the threshold (titan-ops architecture/resource-lifecycle.md#lost-and-stale). CommandStatus null:
// no command row carries this request's key — a control-plane bug, not a slow worker.
public record StuckRequestQueryResult(
    string Resource,
    Guid Id,
    Guid TenantId,
    Guid RequestId,
    string Kind,
    DateTimeOffset AskedAt,
    string? CommandStatus)
{
    public static StuckRequestQueryResult From(StuckRequest r) =>
        new(r.Resource, r.Id, r.TenantId, r.RequestId, r.Kind, r.AskedAt, r.CommandStatus);
}
