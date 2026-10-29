using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Billing.Wallet.Contracts;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Repositories.Billing;

namespace Titan.Application.Queries.Billing.Wallet;

/// Every movement (TopUp +, promo +, spend −) in chronological order with the balance after each
/// (titan-ops architecture/money-and-wallet.md#wallet-one-balance).
public class GetWalletStatementQueryHandler(IWalletRepository walletRepo, ICurrentUser currentUser, CreditOptions options)
{
    public async Task<WalletStatementQueryResult> HandleAsync(GetWalletStatementQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();

        // One read of the signed ledger: inflows and spending come from the same table.
        var movements = await walletRepo.ListTransactionsByTenantAsync(tenantId, ct);

        var lines = movements
            .Select(m => new WalletStatementLine(m.CreatedAt, Label(m.Type), m.Amount, m.BalanceAfter))
            .ToList();

        var closing = lines.Count == 0 ? 0m : lines[^1].RunningBalance;
        return new WalletStatementQueryResult(0m, closing, options.Currency, lines);
    }

    /// The customer's word for a movement, not ours: InvoicePayment has no business on the wire.
    private static string Label(WalletTransactionType type) => type switch
    {
        WalletTransactionType.TopUp => "TopUp",
        WalletTransactionType.Promo => "Promo",
        WalletTransactionType.InvoicePayment => "Spend",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown wallet movement.")
    };
}
