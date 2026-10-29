namespace Titan.Application.Commands.Billing.Invoice.Contracts;

// titan-ops architecture/money-and-wallet.md#human-confirmed-debit — the tenant pays one of its own invoices from its wallet.
public record PayInvoiceFromWalletCommand(Guid InvoiceId);
