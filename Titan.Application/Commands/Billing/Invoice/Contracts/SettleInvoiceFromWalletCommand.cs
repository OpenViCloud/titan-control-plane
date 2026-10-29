namespace Titan.Application.Commands.Billing.Invoice.Contracts;

// titan-ops architecture/money-and-wallet.md#human-confirmed-debit — an admin spends the tenant's wallet on the tenant's behalf.
public record SettleInvoiceFromWalletCommand(Guid InvoiceId);
