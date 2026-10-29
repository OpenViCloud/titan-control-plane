using FastEndpoints;
using Titan.Application.Queries.Billing.TopUp;
using Titan.Application.Queries.Billing.TopUp.Contracts;

namespace Titan.Infrastructure.Endpoints.TopUp;

// The portal filters Confirmed to render receipts (titan-ops architecture/money-and-wallet.md#confirmed-topup-is-receipt).
public class ListMyTopUpsEndpoint(ListMyTopUpsQueryHandler handler)
    : EndpointWithoutRequest<IReadOnlyList<TopUpQueryResult>>
{
    public override void Configure() => Get("v1/tenant/topups");

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendOkAsync(await handler.HandleAsync(new ListMyTopUpsQuery(), ct), ct);
}
