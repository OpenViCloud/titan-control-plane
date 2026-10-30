using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// Invoice is a frozen snapshot of the UsageRecord ledger at generation time
// (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot).
public class InvoiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static InvoiceItem Item(long amount, UsageResourceType resourceType = UsageResourceType.Compute) =>
        InvoiceItem.Create(resourceType, Guid.NewGuid(), "web-01", amount, "SGD");

    private static Invoice Generate(IReadOnlyList<InvoiceItem> items, DateTimeOffset? issuedAt = null) =>
        Invoice.Generate(TenantId, 2026, 8, issuedAt ?? IssuedAt, items, "SGD");

    [Fact]
    public void Generate_SetsDueAtSevenDaysAfterIssued()
    {
        var invoice = Generate([Item(10L)]);

        Assert.Equal(IssuedAt.AddDays(7), invoice.DueAt);
        Assert.Equal(7, Invoice.PaymentTermDays);
    }

    [Fact]
    public void Generate_TotalEqualsSumOfItemAmounts()
    {
        var invoice = Generate([Item(10L), Item(5L, UsageResourceType.Disk)]);

        Assert.Equal(15L, invoice.Total);
    }

    [Fact]
    public void Generate_StatusIsNew()
    {
        var invoice = Generate([Item(10L)]);

        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    // Overdue is derived at read time, never a stored column.
    [Fact]
    public void DisplayStatus_NewPastDue_IsOverdue()
    {
        var invoice = Generate([Item(10L)]);

        var status = invoice.DisplayStatus(IssuedAt.AddDays(8));

        Assert.Equal(InvoiceDisplayStatus.Overdue, status);
    }

    [Fact]
    public void DisplayStatus_NewBeforeDue_IsNew()
    {
        var invoice = Generate([Item(10L)]);

        var status = invoice.DisplayStatus(IssuedAt.AddDays(1));

        Assert.Equal(InvoiceDisplayStatus.New, status);
    }

    [Fact]
    public void MarkPaid_FromNew_SetsPaidAndActor()
    {
        var invoice = Generate([Item(10L)]);
        var paidAt = IssuedAt.AddDays(3);

        invoice.MarkPaid(paidAt, "admin-1");

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(paidAt, invoice.PaidAt);
        Assert.Equal("admin-1", invoice.PaidBy);
    }

    // Paying an overdue invoice must report Paid, not Overdue, even though now > DueAt.
    [Fact]
    public void DisplayStatus_Paid_IsPaidEvenPastDue()
    {
        var invoice = Generate([Item(10L)]);
        invoice.MarkPaid(IssuedAt.AddDays(20), "admin-1");

        var status = invoice.DisplayStatus(IssuedAt.AddDays(30));

        Assert.Equal(InvoiceDisplayStatus.Paid, status);
    }

    [Fact]
    public void MarkPaid_WhenAlreadyPaid_Throws_INVOICE_ALREADY_PAID()
    {
        var invoice = Generate([Item(10L)]);
        invoice.MarkPaid(IssuedAt.AddDays(1), "admin-1");

        var ex = Assert.Throws<BusinessException>(() => invoice.MarkPaid(IssuedAt.AddDays(2), "admin-2"));

        Assert.Equal("INVOICE_ALREADY_PAID", ex.Code);
        // Unchanged: the second call must not overwrite who/when it was actually paid.
        Assert.Equal(IssuedAt.AddDays(1), invoice.PaidAt);
        Assert.Equal("admin-1", invoice.PaidBy);
    }

    // An item's Amount is fixed at generation; nothing on Invoice recomputes it from UsageRecord after
    // the fact, so it must simply hold what Create was given.
    [Fact]
    public void Item_Amount_IsFrozen_NotRecomputed()
    {
        var item = Item(42L);
        var invoice = Generate([item]);

        Assert.Equal(42L, invoice.Items.Single().Amount);
        Assert.Equal(invoice.Id, invoice.Items.Single().InvoiceId);
    }

    // ── titan-ops architecture/money-and-wallet.md#human-confirmed-debit — settled by wallet (only ever by an explicit human action) ──

    // A wallet-settled invoice displays "Settled" and NEVER falls into Overdue, even long past its due
    // date — the customer owes nothing more, so there is nothing left to remind them about.
    [Fact]
    public void DisplayStatus_SettledFromWallet_IsSettled_EvenPastDue()
    {
        var invoice = Generate([Item(100L)]);
        invoice.SettleFromWallet(IssuedAt.AddDays(1));

        Assert.Equal(InvoiceDisplayStatus.Settled, invoice.DisplayStatus(IssuedAt.AddDays(30)));
    }

    // The payment time is stamped, never the issue time: the wallet statement places the spend on the
    // day the customer actually paid.
    [Fact]
    public void SettleFromWallet_FromNew_StampsPaidAtGivenTime_ByWallet()
    {
        var issuedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var invoice = Generate([Item(100L)], issuedAt);
        var paidAt = issuedAt.AddDays(10);

        invoice.SettleFromWallet(paidAt);

        Assert.Equal(InvoiceStatus.SettledByWallet, invoice.Status);
        Assert.Equal(paidAt, invoice.PaidAt);
        Assert.Equal("wallet", invoice.PaidBy);
        Assert.NotEqual(invoice.IssuedAt, invoice.PaidAt);
    }

    // Overdue must NOT block payment: overdue is only "New past DueAt", never a stored status.
    [Fact]
    public void SettleFromWallet_OverdueInvoice_Settles_AndStopsBeingOverdue()
    {
        var issuedAt = DateTimeOffset.UtcNow.AddDays(-40);
        var invoice = Generate([Item(100L)], issuedAt);
        Assert.Equal(InvoiceDisplayStatus.Overdue, invoice.DisplayStatus(DateTimeOffset.UtcNow));

        invoice.SettleFromWallet(DateTimeOffset.UtcNow);

        Assert.Equal(InvoiceDisplayStatus.Settled, invoice.DisplayStatus(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SettleFromWallet_WhenAlreadyTerminal_Throws()
    {
        // Both terminal states are guarded, so a second wallet payment can never pay twice.
        var paid = Generate([Item(100L)]);
        paid.MarkPaid(DateTimeOffset.UtcNow, "admin");
        Assert.Equal("INVOICE_INVALID_STATE",
            Assert.Throws<BusinessException>(() => paid.SettleFromWallet(DateTimeOffset.UtcNow)).Code);

        var settled = Generate([Item(100L)]);
        settled.SettleFromWallet(DateTimeOffset.UtcNow);
        Assert.Equal("INVOICE_INVALID_STATE",
            Assert.Throws<BusinessException>(() => settled.SettleFromWallet(DateTimeOffset.UtcNow)).Code);
    }

    // ── titan-ops architecture/money-and-wallet.md#human-confirmed-debit — a zero-total invoice settles itself ───────────────

    [Fact]
    public void AutoSettleZeroTotal_FromNew_StampsPaidBySystem_AtIssueTime()
    {
        var invoice = Generate([Item(0L)]);

        invoice.AutoSettleZeroTotal();

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal("system", invoice.PaidBy);        // nobody paid; no admin recorded anything either
        Assert.Equal(invoice.IssuedAt, invoice.PaidAt);
        Assert.Equal(InvoiceDisplayStatus.Paid, invoice.DisplayStatus(invoice.DueAt.AddDays(30)));
    }

    // Keeps the zero-total auto-settle from becoming a way around a human-confirmed debit: anything above
    // zero moves real money and needs a person. Without it, one wrong caller silently pays bills for free.
    [Fact]
    public void AutoSettleZeroTotal_NonZeroTotal_Throws()
    {
        var invoice = Generate([Item(100L)]);

        var ex = Assert.Throws<BusinessException>(() => invoice.AutoSettleZeroTotal());
        Assert.Equal("INVOICE_INVALID_STATE", ex.Code);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    [Fact]
    public void AutoSettleZeroTotal_WhenAlreadyTerminal_Throws()
    {
        var invoice = Generate([Item(0L)]);
        invoice.AutoSettleZeroTotal();

        Assert.Equal("INVOICE_INVALID_STATE",
            Assert.Throws<BusinessException>(() => invoice.AutoSettleZeroTotal()).Code);
    }

    // A wallet-settled invoice is terminal — it cannot then be marked paid (no double-settlement).
    [Fact]
    public void MarkPaid_WhenSettledByWallet_Throws_INVOICE_ALREADY_PAID()
    {
        var invoice = Generate([Item(100L)]);
        invoice.SettleFromWallet(IssuedAt.AddDays(1));

        var ex = Assert.Throws<BusinessException>(() => invoice.MarkPaid(IssuedAt.AddDays(2), "admin-1"));
        Assert.Equal("INVOICE_ALREADY_PAID", ex.Code);
    }

    // ── a display label cannot be assigned to the stored state ──

    // The stored type has ONLY the three persistable values, so `Invoice.Status = Overdue` (or Settled) is a
    // compile error, not a runtime bug. Re-adding Overdue/Settled to the stored enum fails here.
    [Fact]
    public void StoredStatus_HasExactlyThreePersistableValues_AndNoDisplayLabels()
    {
        Assert.True(typeof(InvoiceStatus).IsEnum);

        Assert.Equal(
            new[] { "New", "Paid", "SettledByWallet" }.ToHashSet(),
            Enum.GetNames<InvoiceStatus>().ToHashSet());

        Assert.False(Enum.IsDefined(typeof(InvoiceStatus), "Overdue" as object));
        Assert.False(Enum.IsDefined(typeof(InvoiceStatus), "Settled" as object));
    }

    // The display projection is a distinct type carrying the two read-time-only labels on top of the two
    // shared ones — never the stored-only SettledByWallet, which shows to customers as "Settled".
    [Fact]
    public void DisplayStatus_HasExactlyTheFourDisplayLabels()
    {
        Assert.True(typeof(InvoiceDisplayStatus).IsEnum);

        Assert.Equal(
            new[] { "New", "Paid", "Settled", "Overdue" }.ToHashSet(),
            Enum.GetNames<InvoiceDisplayStatus>().ToHashSet());

        Assert.False(Enum.IsDefined(typeof(InvoiceDisplayStatus), "SettledByWallet" as object));
    }

    // Usage is never negative, so neither is a line built from it (titan-ops architecture/money-and-wallet.md#money-is-long-vnd:
    // non-negative is each aggregate's invariant, not the column's). Zero is real usage of nothing.
    [Fact]
    public void Item_WithANegativeAmount_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Item(-1L));
    }

    [Fact]
    public void Item_WithZero_IsALine()
    {
        Assert.Equal(0L, Item(0L).Amount);
    }
}
