using FastEndpoints;
using Titan.Application.Queries.Billing.Wallet;
using Titan.Application.Queries.Billing.Wallet.Contracts;

namespace Titan.Infrastructure.Endpoints.Wallet;

// titan-ops architecture/http-contract.md#tenant-scoping
public class GetWalletStatementEndpoint(GetWalletStatementQueryHandler handler)
    : EndpointWithoutRequest<WalletStatementQueryResult>
{
    public override void Configure() => Get("v1/tenant/wallet/statement");

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendOkAsync(await handler.HandleAsync(new GetWalletStatementQuery(), ct), ct);
}
