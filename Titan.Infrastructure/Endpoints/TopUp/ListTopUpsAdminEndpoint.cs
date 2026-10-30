using FastEndpoints;
using Titan.Application.Queries.Billing.TopUp;
using Titan.Application.Queries.Billing.TopUp.Contracts;
using Titan.Domain.Aggregates.TopUp;

namespace Titan.Infrastructure.Endpoints.TopUp;

// `?source=` filters by Source (PromotionalCredit = the grant history); omitted = every TopUp.
public record ListTopUpsAdminRequest([property: QueryParam] string? Source);

public class ListTopUpsAdminEndpoint(ListTopUpsQueryHandler handler)
    : Endpoint<ListTopUpsAdminRequest, IReadOnlyList<TopUpQueryResult>>
{
    public override void Configure()
    {
        Get("v1/admin/topups");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(ListTopUpsAdminRequest req, CancellationToken ct)
    {
        TopUpSource? source = null;
        if (!string.IsNullOrWhiteSpace(req.Source))
        {
            // Enum.TryParse accepts any number ("9"), so a parsed value that names no member is still unknown.
            bool known = Enum.TryParse<TopUpSource>(req.Source, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed);
            if (!known)
            {
                AddError(r => r.Source, $"Unknown TopUp source '{req.Source}'.");
                await SendErrorsAsync(400, ct);
                return;
            }
            source = parsed;
        }

        await SendOkAsync(await handler.HandleAsync(new ListTopUpsQuery(source), ct), ct);
    }
}
