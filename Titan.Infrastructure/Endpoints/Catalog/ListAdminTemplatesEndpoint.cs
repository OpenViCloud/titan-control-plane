using FastEndpoints;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

// Separate from the customer GET /v1/templates (existing aliases only): AdminTemplateQueryResult exposes the
// sync of every cluster, which TemplateQueryResult hides.
public class ListAdminTemplatesEndpoint(ListAdminTemplatesQueryHandler handler)
    : EndpointWithoutRequest<List<AdminTemplateQueryResult>>
{
    public override void Configure()
    {
        Get("v1/admin/templates");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListAdminTemplatesQuery(), ct), cancellation: ct);
    }
}
