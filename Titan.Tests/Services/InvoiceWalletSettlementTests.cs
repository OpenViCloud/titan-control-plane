using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;

namespace Titan.Tests.Services;

// A press that passed the repeat check and then finds another press's debit
// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). The fake repository of InvoiceServiceTests answers both
// reads from one dictionary, so a row cannot change between them there. Silent about real timing:
// InvoiceDebitRetryDbTests runs two presses on Postgres.
public class InvoiceWalletSettlementTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<IWalletRepository> _wallets = new();
    private readonly Mock<IWalletMutationRepository> _mutations = new();
    private readonly Mock<IWalletMutationPublisher> _bus = new();
    private readonly Wallet _wallet = Wallet.Open(TenantId, "VND");
    private readonly InvoiceAggregate _invoice = InvoiceAggregate.Generate(
        TenantId, 2026, 4, DateTimeOffset.UtcNow,
        [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-01", 300_000L, "VND")], "VND");

    public InvoiceWalletSettlementTests()
    {
        _wallets.Setup(w => w.GetBalanceAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(500_000L);
        _wallets.Setup(w => w.GetByTenantForUpdateAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(_wallet);
    }

    private WalletMutation Debit() =>
        WalletMutation.Create(WalletMutationType.Debit, _wallet.Id, _invoice.Total, WalletMutation.InvoiceRefType, _invoice.Id);

    private void TheServiceFinds(WalletMutation row) =>
        _mutations.Setup(m => m.GetForRefForUpdateAsync(WalletMutation.InvoiceRefType, _invoice.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);

    private void TheReadBackFinds(WalletMutation row) =>
        _mutations.Setup(m => m.GetForRefAsync(WalletMutation.InvoiceRefType, _invoice.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);

    private Task SettleAsync() => InvoiceWalletSettlement.SettleAsync(
        new InvoiceWalletPayment(_wallets.Object, _mutations.Object),
        _wallets.Object, _mutations.Object, _bus.Object, _invoice, default);

    private void NothingWasSavedOrSent()
    {
        _wallets.Verify(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _bus.Verify(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ADebitAnotherPressLeftWaiting_AnswersLikeTheFirstPress()
    {
        WalletMutation waiting = Debit();
        TheServiceFinds(waiting);
        TheReadBackFinds(waiting);

        await SettleAsync();

        NothingWasSavedOrSent();
    }

    [Fact]
    public async Task ADebitAnotherPressAlreadyApplied_AnswersLikeTheFirstPress()
    {
        WalletMutation applied = Debit();
        applied.MarkApplied();
        TheServiceFinds(applied);
        TheReadBackFinds(applied);

        await SettleAsync();

        NothingWasSavedOrSent();
    }

    [Fact]
    public async Task ADebitThatFailedBetweenTheTwoReads_IsAConflict_NotARepeat()
    {
        WalletMutation failed = Debit();
        failed.MarkFailed("the wallet was short");
        TheServiceFinds(Debit());
        TheReadBackFinds(failed);

        var ex = await Assert.ThrowsAsync<ConflictException>(SettleAsync);

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        NothingWasSavedOrSent();
    }
}
