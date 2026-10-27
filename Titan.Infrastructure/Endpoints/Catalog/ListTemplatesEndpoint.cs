using FastEndpoints;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

// A bound request, not a read of the raw query string, so the spec declares clusterId and generated clients carry it.
public record ListTemplatesRequest([property: QueryParam] string? ClusterId);

// https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-customer-read
public class ListTemplatesEndpoint(ListTemplatesQueryHandler handler) : Endpoint<ListTemplatesRequest, List<TemplateQueryResult>>
{
    public override void Configure()
    {
        Get("v1/templates");
    }

    public override async Task HandleAsync(ListTemplatesRequest req, CancellationToken ct)
    {
        await SendAsync(await handler.HandleAsync(new ListTemplatesQuery(req.ClusterId), ct), cancellation: ct);
    }
}
