using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;

namespace Titan.Tests.Domain;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox. The rule alone; WalletMutationConsumerTests runs the
// consumer around it (a redelivered notify, the one save), WalletMutationConsumerDbTests the transaction on Postgres.
public class WalletMutationApplyTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<IWalletRepository> _wallets = new();
    private readonly Mock<IInvoiceRepository> _invoices = new();
    private readonly Mock<IWalletTransactionRepository> _transactions = new();
    private readonly Wallet _wallet = Wallet.Open(TenantId, "VND");

    public WalletMutationApplyTests() =>
        _wallets.Setup(w => w.GetByIdForUpdateAsync(_wallet.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_wallet);

    private WalletMutationApply Apply() => new(_wallets.Object, _invoices.Object, new WalletLedger(_transactions.Object));

    private InvoiceAggregate Owed(long total)
    {
        InvoiceItem item = InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-01", total, "VND");
        InvoiceAggregate invoice = InvoiceAggregate.Generate(TenantId, 2026, 4, DateTimeOffset.UtcNow, [item], "VND");
        _invoices.Setup(i => i.GetForUpdateAsync(invoice.Id, It.IsAny<CancellationToken>())).ReturnsAsync(invoice);
        return invoice;
    }

    private WalletMutation DebitFor(InvoiceAggregate invoice) =>
        WalletMutation.Create(WalletMutationType.Debit, _wallet.Id, invoice.Total, WalletMutation.InvoiceRefType, invoice.Id);

    private void HoldsAtLeast(long amount) => _wallet.Credit(amount);

    [Fact]
    public async Task Debit_Covered_MovesTheMoney_SettlesTheInvoice_AndApplies()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        HoldsAtLeast(500_000L);
        WalletMutation debit = DebitFor(invoice);

        await Apply().ApplyAsync(debit);

        Assert.Equal(500_000L - invoice.Total, _wallet.AvailableAmount);
        Assert.Equal(InvoiceStatus.SettledByWallet, invoice.Status);
        Assert.Equal(WalletMutationStatus.Applied, debit.Status);
        _transactions.Verify(t => t.Add(It.IsAny<WalletTransaction>()), Times.Once);
    }

    [Fact]
    public async Task Debit_ShortByADong_FailsTheMutation_AndMovesNothing()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        HoldsAtLeast(invoice.Total - 1);
        WalletMutation debit = DebitFor(invoice);

        await Apply().ApplyAsync(debit);

        Assert.Equal(invoice.Total - 1, _wallet.AvailableAmount);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Equal(WalletMutationStatus.Failed, debit.Status);
        _transactions.Verify(t => t.Add(It.IsAny<WalletTransaction>()), Times.Never);
    }

    [Fact]
    public async Task Debit_ForAnInvoiceNoLongerOwed_FailsTheMutation_AndMovesNothing()
    {
        InvoiceAggregate invoice = Owed(300_000L);
        HoldsAtLeast(500_000L);
        WalletMutation debit = DebitFor(invoice);
        invoice.MarkPaid(DateTimeOffset.UtcNow, "admin");

        await Apply().ApplyAsync(debit);

        Assert.Equal(500_000L, _wallet.AvailableAmount);
        Assert.Equal(WalletMutationStatus.Failed, debit.Status);
        _transactions.Verify(t => t.Add(It.IsAny<WalletTransaction>()), Times.Never);
    }

    [Fact]
    public async Task Credit_MovesTheMoney_AndApplies()
    {
        WalletMutation credit = WalletMutation.Create(WalletMutationType.Credit, _wallet.Id, 100_000L, WalletMutation.TopUpRefType, Guid.NewGuid());

        await Apply().ApplyAsync(credit);

        Assert.Equal(100_000L, _wallet.AvailableAmount);
        Assert.Equal(WalletMutationStatus.Applied, credit.Status);
    }

    [Fact]
    public async Task AMutationForAWalletThatDoesNotExist_IsNotFound()
    {
        WalletMutation credit = WalletMutation.Create(WalletMutationType.Credit, Guid.NewGuid(), 100_000L, WalletMutation.TopUpRefType, Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Apply().ApplyAsync(credit));

        Assert.Equal("WALLET_NOT_FOUND", ex.Code);
    }

    [Fact] // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the consumer recognises the repeat first
    public async Task AMutationNoLongerPending_IsRefused_AndMovesNothing()
    {
        WalletMutation credit = WalletMutation.Create(WalletMutationType.Credit, _wallet.Id, 100_000L, WalletMutation.TopUpRefType, Guid.NewGuid());
        credit.MarkApplied();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply().ApplyAsync(credit));

        Assert.Equal(0L, _wallet.AvailableAmount);
    }

    [Fact]
    public void OnlyTheApplyMarksAMutationApplied()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] mutationMethods = [.. typeof(WalletMutation).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(WalletMutation.RecordAttempt), mutationMethods);
        Assert.DoesNotContain(nameof(WalletMutation.MarkApplied), mutationMethods);
    }
}
