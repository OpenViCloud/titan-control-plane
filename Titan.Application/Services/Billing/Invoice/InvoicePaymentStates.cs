using Titan.Application.Services.Billing.WalletMutations;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Billing.Invoice;

// Three query handlers show an invoice's payment state, and handlers may not call each other
// (ReadWriteSeparationTests.Handlers_DoNotReferenceOtherHandlers)
// (titan-ops architecture/code-structure.md#services-branch).
public static class InvoicePaymentStates
{
    /// Read from the outbox mutations raised against the invoices. An invoice nobody has paid has no
    /// mutation, so it is absent from the dictionary and reads as null on the DTO.
    public static async Task<Dictionary<Guid, PaymentState?>> LookUpAsync(
        IWalletMutationRepository mutationRepo, IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct)
    {
        var mutations = await mutationRepo.GetForRefsAsync(
            WalletMutationConsumer.InvoiceRefType, invoiceIds, ct);

        return mutations.ToDictionary(kv => kv.Key, kv => From(kv.Value.Status));
    }

    /// The outbox mutation's lifecycle as the customer sees it. Not a cast: the two enums answer
    /// different questions and are free to diverge — Pending is machinery, Processing is a promise.
    public static PaymentState? From(WalletMutationStatus status) => status switch
    {
        WalletMutationStatus.Pending => PaymentState.Processing,
        WalletMutationStatus.Applied => PaymentState.Settled,
        WalletMutationStatus.Failed => PaymentState.Failed,
        _ => null
    };
}
