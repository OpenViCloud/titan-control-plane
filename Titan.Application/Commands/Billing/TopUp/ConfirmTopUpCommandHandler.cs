using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.TopUp;
using Titan.Application.Commands.Billing.TopUp.Contracts;
using Titan.Domain.Aggregates.Billing.TopUp;
using Titan.Domain.Repositories.Billing;

namespace Titan.Application.Commands.Billing.TopUp;

/// The manual fallback: a platform admin records a payment reconciled by hand. The credit is queued,
/// not applied (titan-ops architecture/money-and-wallet.md#single-writer-outbox), so the endpoint answers 202
/// (titan-ops architecture/http-contract.md#async-actions-return-202).
public class ConfirmTopUpCommandHandler(
    ICreditRepository repo,
    IWalletRepository walletRepo,
    IWalletMutationRepository mutationRepo,
    IWalletMutationPublisher walletBus,
    ICurrentUser currentUser)
{
    public Task HandleAsync(ConfirmTopUpCommand command, CancellationToken ct = default) =>
        TopUpConfirmation.ConfirmAsync(
            repo, walletRepo, mutationRepo, walletBus,
            command.TopUpId, TopUpSource.Manual, currentUser.KeycloakUserId, command.PaymentTxnRef,
            confirmedAt: null, ct);
}
