namespace Titan.Application.Commands.Billing.Invoice.Contracts;

// Money that arrived OUTSIDE Titan (titan-ops architecture/money-and-wallet.md#human-confirmed-debit) — the wallet is never touched.
public record MarkInvoicePaidCommand(Guid InvoiceId);
