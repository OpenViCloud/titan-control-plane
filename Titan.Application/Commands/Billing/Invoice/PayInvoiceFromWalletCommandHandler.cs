using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Application.Commands.Billing.Invoice;

/// The tenant pays one of its own invoices from its wallet because it pressed a button saying so
/// (titan-ops architecture/money-and-wallet.md#human-confirmed-debit).
public class PayInvoiceFromWalletCommandHandler(
    IInvoiceRepository invoiceRepo,
    IWalletRepository walletRepo,
    IWalletMutationRepository mutationRepo,
    IWalletMutationPublisher walletBus,
    InvoiceWalletPayment payment,
    ICurrentUser currentUser)
{
    public async Task HandleAsync(PayInvoiceFromWalletCommand command, CancellationToken ct = default)
    {
        var invoice = await InvoiceOwnership.RequireInvoiceForUpdateAsync(
            invoiceRepo, currentUser.RequireTenantId(), command.InvoiceId, ct);

        // A repeat is recognised here first (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat):
        // answer like the first press, nothing enqueued, nothing charged.
        bool alreadyPaidFromWallet = await InvoiceWalletSettlement.IsAlreadyPaidFromWalletAsync(mutationRepo, invoice, ct);
        if (alreadyPaidFromWallet)
        {
            return;
        }

        await InvoiceWalletSettlement.SettleAsync(payment, walletRepo, mutationRepo, walletBus, invoice, ct);
    }
}
