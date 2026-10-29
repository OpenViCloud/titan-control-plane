using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Billing;

namespace Titan.Application.Commands.Billing.Invoice;

/// The tenant pays one of its own invoices from its wallet because it pressed a button saying so
/// (titan-ops architecture/money-and-wallet.md#human-confirmed-debit).
public class PayInvoiceFromWalletCommandHandler(
    IInvoiceRepository invoiceRepo,
    IWalletRepository walletRepo,
    IWalletMutationRepository mutationRepo,
    IWalletMutationPublisher walletBus,
    ICurrentUser currentUser)
{
    public async Task HandleAsync(PayInvoiceFromWalletCommand command, CancellationToken ct = default)
    {
        var invoiceId = command.InvoiceId;
        var tenantId = currentUser.RequireTenantId();
        var invoice = await invoiceRepo.GetForUpdateAsync(invoiceId, ct);

        // Same error for "does not exist" and "belongs to another tenant", so a payment attempt cannot
        // probe for invoice ids.
        if (invoice is null || invoice.TenantId != tenantId)
            throw new NotFoundException("INVOICE_NOT_FOUND", $"Invoice {invoiceId} not found.");

        // A repeat is recognised here first (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat):
        // answer like the first press, nothing enqueued, nothing charged.
        bool alreadyPaidFromWallet = await InvoiceWalletSettlement.IsAlreadyPaidFromWalletAsync(mutationRepo, invoice, ct);
        if (alreadyPaidFromWallet)
        {
            return;
        }

        await InvoiceWalletSettlement.SettleAsync(walletRepo, mutationRepo, walletBus, invoice, ct);
    }
}
