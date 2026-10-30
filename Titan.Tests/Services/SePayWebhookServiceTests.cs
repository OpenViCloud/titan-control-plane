using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction.Contracts;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

// Attribute an inbound payment to a TopUp and credit it exactly once, or record an anomaly. Retry
// dedupe on a real database is proven in SePayWebhookDbTests; these pin the branching (match /
// duplicate / orphan / mismatch / ignore). TopUpConfirmation runs for real, so "confirmed" is asserted
// on what it leaves behind — the TopUp's Source/ConfirmedBy/ConfirmedAt and the one credit mutation.
public class SePayWebhookServiceTests
{
    private readonly Mock<IPaymentGatewayTransactionRepository> _payments = new();
    private readonly Mock<ICreditRepository> _credit = new();
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<IWalletMutationRepository> _mutationRepo = new();
    private readonly Mock<IWalletMutationPublisher> _bus = new();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset TxnDate = new(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);

    public SePayWebhookServiceTests()
    {
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Wallet.Open(Tenant, "VND"));
        _walletRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _bus.Setup(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private ReceiveSePayPaymentCommandHandler CreateService() =>
        new(_payments.Object, _credit.Object, _walletRepo.Object, _mutationRepo.Object, _bus.Object,
            NullLogger<ReceiveSePayPaymentCommandHandler>.Instance);

    private void AssertNothingCredited() =>
        _mutationRepo.Verify(r => r.Add(It.IsAny<WalletMutation>()), Times.Never);

    private static ReceiveSePayPaymentCommand Payload(
        string id = "sepay-1", string type = "in", decimal amount = 900000m, string? memo = "Nap TITAN-7F3KQ9A2 xin cam on") =>
        new(id, "Vietcombank", TxnDate, type, amount, memo, null);

    private static TopUp PendingTopUp(decimal amount = 900000m)
    {
        var t = TopUp.Create(Tenant, (long)amount, "VND");
        SetRef(t, "TITAN-7F3KQ9A2");
        return t;
    }

    private static void SetRef(TopUp t, string re) =>
        typeof(TopUp).GetProperty(nameof(TopUp.PaymentRef))!.SetValue(t, re);

    private PaymentGatewayTransaction? _recorded;
    private void CaptureRecord() =>
        _payments.Setup(p => p.TryAddAsync(It.IsAny<PaymentGatewayTransaction>(), It.IsAny<CancellationToken>()))
                 .Callback<PaymentGatewayTransaction, CancellationToken>((pt, _) => _recorded = pt)
                 .ReturnsAsync(true);

    // A matching inbound payment of the right amount confirms the TopUp and records Matched.
    [Fact]
    public async Task Match_In_CorrectAmount_ConfirmsTopUp_RecordsMatched()
    {
        var topUp = PendingTopUp();
        _payments.Setup(p => p.ExistsAsync("sepay-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _credit.Setup(c => c.GetTopUpByPaymentRefAsync("TITAN-7F3KQ9A2", It.IsAny<CancellationToken>())).ReturnsAsync(topUp);
        _credit.Setup(c => c.GetTopUpForUpdateAsync(topUp.Id, It.IsAny<CancellationToken>())).ReturnsAsync(topUp);
        CaptureRecord();

        var outcome = await CreateService().HandleAsync(Payload());

        Assert.Equal(SePayWebhookOutcome.Matched, outcome);
        // Confirmed at the payment's transactionDate, not now; audited as sepay:<txn.id>.
        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
        Assert.Equal(TopUpSource.SePay, topUp.Source);
        Assert.Equal("sepay:sepay-1", topUp.ConfirmedBy);
        Assert.Equal("sepay-1", topUp.PaymentTxnRef);
        Assert.Equal(TxnDate, topUp.ConfirmedAt);
        _mutationRepo.Verify(r => r.Add(It.Is<WalletMutation>(m => m.RefId == topUp.Id)), Times.Once);
        Assert.NotNull(_recorded);
        Assert.Equal(PaymentGatewayTransactionStatus.Matched, _recorded!.Status);
        Assert.Equal(topUp.Id, _recorded.TopUpId);
    }

    // A retried webhook (same SePay txn id already recorded) is a no-op: no confirm, no record.
    [Fact]
    public async Task Retry_SameTxnId_Deduped_NoConfirm_NoRecord()
    {
        _payments.Setup(p => p.ExistsAsync("sepay-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var outcome = await CreateService().HandleAsync(Payload());

        Assert.Equal(SePayWebhookOutcome.Duplicate, outcome);
        AssertNothingCredited();
        _payments.Verify(p => p.TryAddAsync(It.IsAny<PaymentGatewayTransaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The TopUp was already Confirmed (e.g. admin first): record Duplicate, do not re-credit.
    [Fact]
    public async Task TopUpAlreadyConfirmed_RecordsDuplicate_NoRecredit()
    {
        var topUp = PendingTopUp();
        topUp.Confirm(TopUpSource.Manual, "admin", "bank-x");
        _payments.Setup(p => p.ExistsAsync("sepay-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _credit.Setup(c => c.GetTopUpByPaymentRefAsync("TITAN-7F3KQ9A2", It.IsAny<CancellationToken>())).ReturnsAsync(topUp);
        CaptureRecord();

        var outcome = await CreateService().HandleAsync(Payload());

        Assert.Equal(SePayWebhookOutcome.Duplicate, outcome);
        Assert.Equal(PaymentGatewayTransactionStatus.Duplicate, _recorded!.Status);
        AssertNothingCredited();
    }

    // No TopUp carries the referenced code: record Orphan, no credit, no tenant leak.
    [Fact]
    public async Task NoMatchingRef_RecordsOrphan_NoConfirm()
    {
        _payments.Setup(p => p.ExistsAsync("sepay-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _credit.Setup(c => c.GetTopUpByPaymentRefAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((TopUp?)null);
        CaptureRecord();

        var outcome = await CreateService().HandleAsync(Payload(memo: "random transfer no ref"));

        Assert.Equal(SePayWebhookOutcome.Orphan, outcome);
        Assert.Equal(PaymentGatewayTransactionStatus.Orphan, _recorded!.Status);
        Assert.Null(_recorded.TopUpId);
        Assert.Null(_recorded.TenantId);
        AssertNothingCredited();
    }

    // A matched TopUp but a different amount: record AmountMismatch, do not credit.
    [Fact]
    public async Task AmountMismatch_RecordsMismatch_NoConfirm()
    {
        var topUp = PendingTopUp(900000m);
        _payments.Setup(p => p.ExistsAsync("sepay-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _credit.Setup(c => c.GetTopUpByPaymentRefAsync("TITAN-7F3KQ9A2", It.IsAny<CancellationToken>())).ReturnsAsync(topUp);
        CaptureRecord();

        var outcome = await CreateService().HandleAsync(Payload(amount: 500000m)); // paid less than the purchase

        Assert.Equal(SePayWebhookOutcome.AmountMismatch, outcome);
        Assert.Equal(PaymentGatewayTransactionStatus.AmountMismatch, _recorded!.Status);
        AssertNothingCredited();
    }

    // An outbound transfer is ignored entirely: no lookup, no record.
    [Fact]
    public async Task TransferOut_Ignored()
    {
        var outcome = await CreateService().HandleAsync(Payload(type: "out"));

        Assert.Equal(SePayWebhookOutcome.Ignored, outcome);
        _payments.Verify(p => p.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _payments.Verify(p => p.TryAddAsync(It.IsAny<PaymentGatewayTransaction>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
