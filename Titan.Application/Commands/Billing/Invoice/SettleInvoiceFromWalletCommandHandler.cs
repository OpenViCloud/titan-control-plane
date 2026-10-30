using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Billing.Invoice;

/// An admin pays from the tenant's wallet on the tenant's behalf (titan-ops architecture/money-and-wallet.md#human-confirmed-debit).
/// Not MarkInvoicePaid: that one records money that arrived outside Titan and never touches the wallet.
public class SettleInvoiceFromWalletCommandHandler(
    IInvoiceRepository invoiceRepo,
    IWalletRepository walletRepo,
    IWalletMutationRepository mutationRepo,
    IWalletMutationPublisher walletBus,
    IAdminAuditLogRepository auditLog,
    ICurrentUser currentUser)
{
    public async Task HandleAsync(SettleInvoiceFromWalletCommand command, CancellationToken ct = default)
    {
        var invoiceId = command.InvoiceId;
        // No tenant scoping: an admin is allowed to know the id does not exist.
        var invoice = await invoiceRepo.GetForUpdateAsync(invoiceId, ct)
            ?? throw new NotFoundException("INVOICE_NOT_FOUND", $"Invoice {invoiceId} not found.");

        // A repeat is recognised here first (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat):
        // answer like the first press, nothing enqueued, nothing charged.
        bool alreadyPaidFromWallet = await InvoiceWalletSettlement.IsAlreadyPaidFromWalletAsync(mutationRepo, invoice, ct);
        if (alreadyPaidFromWallet)
        {
            return;
        }

        // Staged before SettleAsync so the row commits in its SaveChanges, together with the debit request
        // (titan-ops architecture/tenancy-and-admin.md#admin-audit). A refused settle throws before that save and
        // leaves no row: nothing was spent.
        auditLog.Add(AdminAuditLog.Create(
            "invoice.settle_from_wallet", currentUser.KeycloakUserId, "invoice", invoice.Id, invoice.TenantId));

        await InvoiceWalletSettlement.SettleAsync(walletRepo, mutationRepo, walletBus, invoice, ct);
    }
}
