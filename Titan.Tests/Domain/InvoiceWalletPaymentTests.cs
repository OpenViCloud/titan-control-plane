using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;

namespace Titan.Tests.Domain;

// titan-ops architecture/money-and-wallet.md#human-confirmed-debit. Silent about two presses at once: the row's status
// token and UNIQUE(ref_type, ref_id) answer those, InvoiceDebitRetryDbTests runs them on Postgres.
public class InvoiceWalletPaymentTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<IWalletRepository> _wallets = new();
    private readonly Mock<IWalletMutationRepository> _mutations = new();
    private readonly Wallet _wallet = Wallet.Open(TenantId, "VND");

    private InvoiceWalletPayment Payment() => new(_wallets.Object, _mutations.Object);

    private static InvoiceAggregate Owed(long total)
    {
        InvoiceItem item = InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-01", total, "VND");
        return InvoiceAggregate.Generate(TenantId, 2026, 4, DateTimeOffset.UtcNow, [item], "VND");
    }

    private void Holds(long balance)
    {
        _wallets.Setup(w => w.GetBalanceAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(balance);
        _wallets.Setup(w => w.GetByTenantForUpdateAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(_wallet);
    }

    private void HasRow(InvoiceAggregate invoice, WalletMutation row) =>
        _mutations.Setup(m => m.GetForRefForUpdateAsync(WalletMutation.InvoiceRefType, invoice.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);

    private static WalletMutation FailedDebit(InvoiceAggregate invoice, Guid walletId, long amount)
    {
        WalletMutation row = WalletMutation.Create(WalletMutationType.Debit, walletId, amount, WalletMutation.InvoiceRefType, invoice.Id);
        row.MarkFailed("the wallet was short");
        return row;
    }

    [Fact]
    public async Task Request_OwedAndCovered_AddsOnePendingDebitForTheWholeTotal()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(500_000L);

        WalletMutation debit = await Payment().RequestAsync(invoice);

        Assert.Equal(WalletMutationType.Debit, debit.Type);
        Assert.Equal(WalletMutationStatus.Pending, debit.Status);
        Assert.Equal(invoice.Total, debit.Amount);
        Assert.Equal(_wallet.Id, debit.WalletId);
        Assert.Equal(WalletMutation.InvoiceRefType, debit.RefType);
        Assert.Equal(invoice.Id, debit.RefId);
        Assert.False(debit.WasReopened);
        _mutations.Verify(m => m.Add(debit), Times.Once);
    }

    [Fact]
    public async Task Request_AnInvoiceThatIsNotOwed_IsRefused_AndAsksNothing()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        invoice.MarkPaid(DateTimeOffset.UtcNow, "admin");
        Holds(500_000L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => Payment().RequestAsync(invoice));

        Assert.Equal("INVOICE_INVALID_STATE", ex.Code);
        _mutations.Verify(m => m.Add(It.IsAny<WalletMutation>()), Times.Never);
    }

    [Fact]
    public async Task Request_AWalletShortByADong_IsRefused_AndAsksNothing()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(invoice.Total - 1);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => Payment().RequestAsync(invoice));

        Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        _mutations.Verify(m => m.Add(It.IsAny<WalletMutation>()), Times.Never);
    }

    [Fact]
    public async Task Request_ABalanceOfExactlyTheTotal_IsCovered()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(invoice.Total);

        WalletMutation debit = await Payment().RequestAsync(invoice);

        Assert.Equal(WalletMutationStatus.Pending, debit.Status);
    }

    [Fact]
    public async Task Request_AWalletGoneAfterTheBalanceRead_IsAConflict()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        _wallets.Setup(w => w.GetBalanceAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(500_000L);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Payment().RequestAsync(invoice));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task Request_AfterADebitFailed_ReopensThatRow_AndAddsNone()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(500_000L);
        WalletMutation row = FailedDebit(invoice, _wallet.Id, invoice.Total);
        HasRow(invoice, row);

        WalletMutation debit = await Payment().RequestAsync(invoice);

        Assert.Same(row, debit);
        Assert.Equal(WalletMutationStatus.Pending, debit.Status);
        Assert.True(debit.WasReopened);
        _mutations.Verify(m => m.Add(It.IsAny<WalletMutation>()), Times.Never);
    }

    [Fact]
    public async Task Request_AFailedDebitForAnotherAmount_IsAConflict_AndStaysFailed()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(500_000L);
        WalletMutation row = FailedDebit(invoice, _wallet.Id, invoice.Total + 1);
        HasRow(invoice, row);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Payment().RequestAsync(invoice));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        Assert.Equal(WalletMutationStatus.Failed, row.Status);
    }

    [Fact]
    public async Task Request_AFailedDebitOnAnotherWallet_IsAConflict_AndStaysFailed()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(500_000L);
        WalletMutation row = FailedDebit(invoice, Guid.NewGuid(), invoice.Total);
        HasRow(invoice, row);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Payment().RequestAsync(invoice));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        Assert.Equal(WalletMutationStatus.Failed, row.Status);
    }

    [Fact] // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat
    public async Task Request_ADebitAlreadyWaiting_IsRefused_AndChangesNothing()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        Holds(500_000L);
        WalletMutation row = WalletMutation.Create(WalletMutationType.Debit, _wallet.Id, invoice.Total, WalletMutation.InvoiceRefType, invoice.Id);
        HasRow(invoice, row);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Payment().RequestAsync(invoice));

        Assert.Equal(WalletMutationStatus.Pending, row.Status);
        _mutations.Verify(m => m.Add(It.IsAny<WalletMutation>()), Times.Never);
    }

    [Fact]
    public void OnlyTheServicesAskForADebitOrSettleAnInvoice()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] invoiceMethods = [.. typeof(InvoiceAggregate).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(InvoiceAggregate.MarkPaid), invoiceMethods);
        Assert.DoesNotContain(nameof(InvoiceAggregate.SettleFromWallet), invoiceMethods);

        string[] mutationMethods = [.. typeof(WalletMutation).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(WalletMutation.MarkFailed), mutationMethods);
        Assert.DoesNotContain(nameof(WalletMutation.Create), mutationMethods);
        Assert.DoesNotContain(nameof(WalletMutation.ReopenFailedDebit), mutationMethods);
    }
}
