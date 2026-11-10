using Titan.Domain.Aggregates.Invoice;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Application.Services.Billing.Invoice;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Billing.Invoice;

/// Records money that arrived OUTSIDE Titan (titan-ops architecture/money-and-wallet.md#human-confirmed-debit) — the single allowed mutation on an invoice
/// besides settlement. NOT the same as SettleInvoiceFromWallet, which spends the wallet.
public class MarkInvoicePaidCommandHandler(
    IInvoiceRepository invoiceRepo, IWalletMutationRepository mutationRepo, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task HandleAsync(MarkInvoicePaidCommand command, CancellationToken ct = default)
    {
        var id = command.InvoiceId;
        var invoice = await invoiceRepo.GetForUpdateAsync(id, ct)
            ?? throw new NotFoundException("INVOICE_NOT_FOUND", $"Invoice {id} not found.");

        // A second "Mark paid" is a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat):
        // nothing changes, not even who is recorded as having marked it. Invoice.MarkPaid refuses an invoice
        // the wallet paid (INVOICE_ALREADY_PAID): recording outside money on it would count it twice.
        if (invoice.IsMarkedPaid)
        {
            return;
        }

        // A waiting debit keeps the invoice New until the consumer applies it, so Invoice.MarkPaid would accept
        // it and the wallet would then pay the same invoice again.
        if (await InvoiceWalletSettlement.IsDebitWaitingAsync(mutationRepo, invoice, ct))
        {
            throw new ConflictException("INVOICE_PAYMENT_CONFLICT",
                $"Invoice {id} is being paid from the wallet; outside money cannot be recorded on it.");
        }

        invoice.MarkPaid(clock.GetUtcNow(), currentUser.KeycloakUserId);
        await invoiceRepo.SaveChangesAsync(ct);

        // Nothing goes back (titan-ops architecture/http-contract.md#commands-return-no-data), and PaymentState reads null:
        // the wallet is never touched, so no mutation exists. Do not "fix" it for symmetry with
        // SettleFromWallet; the audit log must tell whose money moved.
    }
}
