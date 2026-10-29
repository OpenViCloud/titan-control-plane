using Titan.Application.Services.Billing;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Values;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Application.Queries.Billing.Invoice;
using Titan.Application.Queries.Billing.Invoice.Contracts;

using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.TopUp;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Billing;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;

using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// Acceptance tests — titan-ops features/billing/invoice/user-stories.md#inv-1. The fakes reproduce the DB's
// idempotency/race semantics without a real database.
public class InvoiceServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid OtherTenantId = Guid.NewGuid();
    private static readonly Guid VmId = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 4, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeInvoiceRepository _invoiceRepo = new();
    private readonly FakeUsageRecordRepository _usageRepo = new();
    private readonly FakeWalletRepository _walletRepo = new();

    private readonly Mock<ICurrentUser> _currentUser = new();

    private readonly Mock<ITenantRepository> _tenantRepo = new();

    public InvoiceServiceTests()
    {
        // The receivables view reads tenant names once; empty is the default.
        _tenantRepo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([]);
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);
        _currentUser.Setup(u => u.KeycloakUserId).Returns("admin-1");
    }

    private readonly FakeWalletMutationRepository _mutationRepo = new();
    private readonly FakeWalletMutationPublisher _walletBus = new();
    private readonly FakeAdminAuditLogRepository _auditLog = new();

    // Facade over the invoice handlers; every method forwards to exactly one handler and adds nothing —
    // EXCEPT the three pay methods, which return nothing (titan-ops architecture/http-contract.md#commands-return-no-data).
    // For those the facade does what a real caller does: it reads the invoice back through the query
    // side (the tenant through GetTenantInvoice, an admin through the admin list), so the PaymentState
    // these tests assert comes from the outbox lookup.
    private InvoiceFacade CreateService() =>
        new(_invoiceRepo, _usageRepo, _walletRepo, _currentUser.Object, _tenantRepo.Object,
            _mutationRepo, _walletBus, _auditLog);

    private sealed class InvoiceFacade(
        IInvoiceRepository invoiceRepo,
        IUsageRecordRepository usageRepo,
        IWalletRepository walletRepo,
        ICurrentUser currentUser,
        ITenantRepository tenantRepo,
        IWalletMutationRepository mutationRepo,
        IWalletMutationPublisher walletBus,
        IAdminAuditLogRepository auditLog)
    {
        public Task<GenerateInvoicesForPeriodCommandResult> GenerateForPeriodAsync(int year, int month, CancellationToken ct)
            => new GenerateInvoicesForPeriodCommandHandler(invoiceRepo, usageRepo)
                .HandleAsync(new GenerateInvoicesForPeriodCommand(year, month), ct);

        public Task<IReadOnlyList<InvoiceQueryResult>> GetTenantInvoicesAsync(CancellationToken ct)
            => new ListTenantInvoicesQueryHandler(invoiceRepo, mutationRepo, currentUser)
                .HandleAsync(new ListTenantInvoicesQuery(), ct);

        public Task<InvoiceDetailQueryResult> GetTenantInvoiceAsync(Guid id, CancellationToken ct)
            => new GetTenantInvoiceQueryHandler(invoiceRepo, mutationRepo, currentUser)
                .HandleAsync(new GetTenantInvoiceQuery(id), ct);

        public Task<IReadOnlyList<InvoiceQueryResult>> ListAllAsync(string? status, CancellationToken ct)
            => new ListInvoicesQueryHandler(invoiceRepo, mutationRepo).HandleAsync(new ListInvoicesQuery(status), ct);

        public Task<ReceivablesQueryResult> GetReceivablesAsync(CancellationToken ct = default)
            => new GetReceivablesQueryHandler(invoiceRepo, tenantRepo).HandleAsync(new GetReceivablesQuery(), ct);

        public async Task<InvoiceQueryResult> MarkPaidAsync(Guid id, CancellationToken ct)
        {
            await new MarkInvoicePaidCommandHandler(invoiceRepo, mutationRepo, currentUser).HandleAsync(new MarkInvoicePaidCommand(id), ct);
            return Assert.Single(await ListAllAsync(null, ct), i => i.Id == id);
        }

        public async Task<InvoiceDetailQueryResult> PayFromWalletAsync(Guid id, CancellationToken ct)
        {
            await new PayInvoiceFromWalletCommandHandler(invoiceRepo, walletRepo, mutationRepo, walletBus, currentUser)
                .HandleAsync(new PayInvoiceFromWalletCommand(id), ct);
            return await GetTenantInvoiceAsync(id, ct);
        }

        public async Task<InvoiceQueryResult> SettleFromWalletAsAdminAsync(Guid id, CancellationToken ct)
        {
            await new SettleInvoiceFromWalletCommandHandler(invoiceRepo, walletRepo, mutationRepo, walletBus, auditLog, currentUser)
                .HandleAsync(new SettleInvoiceFromWalletCommand(id), ct);
            return Assert.Single(await ListAllAsync(null, ct), i => i.Id == id);
        }
    }

    // ── GenerateForPeriodAsync ────────────────────────────────────────────

    // One invoice per tenant with usage, one item per (ResourceType, ResourceId).
    [Fact]
    public async Task Generate_TenantWithUsage_CreatesOneInvoiceWithItemPerResource()
    {
        var volumeId = Guid.NewGuid();
        _usageRepo.Seed(
            UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(9000m), "SGD"),
            UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0.AddHours(1), 60, Rate.Of(9000m), "SGD"),
            UsageRecord.CreateDisk(volumeId, "data-vol", TenantId, T0, 60, 40, Rate.Of(50m), "SGD"));

        var result = await CreateService().GenerateForPeriodAsync(2026, 4, default);

        Assert.Equal(1, result.Generated);
        Assert.Equal(0, result.Skipped);
        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(TenantId, invoice.TenantId);
        Assert.Equal(2, invoice.Items.Count);

        var vmItem = invoice.Items.Single(i => i.ResourceId == VmId);
        Assert.Equal(18000L, vmItem.Amount); // 9000 VND/h × two 60-minute compute buckets

        var volItem = invoice.Items.Single(i => i.ResourceId == volumeId);
        Assert.Equal(2000L, volItem.Amount); // 50 VND/GB/h × 1h × 40 GB

        Assert.Equal(invoice.Items.Sum(i => i.Amount), invoice.Total);
    }

    // titan-ops architecture/money-and-wallet.md#round-once — a line is the raw sum of its records, rounded once. Six 10-minute
    // records of a 1 GB volume at 1.57914 ₫/GB/h are 1.57914 ₫ → 2 ₫; rounding each record (0.26319 → 0) billed 0 ₫.
    [Fact]
    public async Task Generate_ALine_IsRoundedOnce_NotPerRecord()
    {
        var volumeId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        for (int tick = 0; tick < 6; tick++)
        {
            _usageRepo.Seed(
                UsageRecord.CreateDisk(volumeId, "small-vol", TenantId, T0.AddMinutes(10 * tick), 10, 1, Rate.Of(1.57914m), "VND"),
                UsageRecord.CreateSnapshot(snapshotId, "snap", TenantId, T0.AddMinutes(10 * tick), 10, Rate.Of(1.8m), "VND"));
        }

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(2L, invoice.Items.Single(i => i.ResourceId == volumeId).Amount);
        Assert.Equal(2L, invoice.Items.Single(i => i.ResourceId == snapshotId).Amount); // 1.8 ₫ → 2 ₫, was 0 ₫
    }

    // ResourceName must come from the UsageRecord snapshot, never a live vms/volumes lookup. Pinned two
    // ways: the value itself, and that the handler cannot even reach IVmRepository.
    [Fact]
    public async Task Generate_ResourceName_ComesFromUsageSnapshot_NotVmTable()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "renamed-since", TenantId, T0, 60, Rate.Of(9000m), "SGD"));

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal("renamed-since", invoice.Items.Single().ResourceName);
    }

    [Fact]
    public void Service_DoesNotDependOnVmRepository()
    {
        var dependencies = typeof(GenerateInvoicesForPeriodCommandHandler)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        Assert.DoesNotContain(typeof(IVmRepository), dependencies);
    }

    // Idempotent: a period that already has an invoice for a tenant is a no-op.
    [Fact]
    public async Task Generate_SecondRun_IsNoOp_WhenPeriodExists()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(9000m), "SGD"));
        _invoiceRepo.SeedExistingPeriod(TenantId, 2026, 4);

        var result = await CreateService().GenerateForPeriodAsync(2026, 4, default);

        Assert.Equal(0, result.Generated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, _invoiceRepo.TryAddCallCount); // never even attempted a write
    }

    // A tenant (or period) with no usage at all gets no invoice, and this is not an error.
    [Fact]
    public async Task Generate_TenantWithoutUsage_SkipsInvoice()
    {
        var result = await CreateService().GenerateForPeriodAsync(2099, 1, default);

        Assert.Equal(0, result.Generated);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(_invoiceRepo.All);
    }

    // A concurrent generation run winning the unique-index race must count as skipped, not crash
    // and not double the count of a "success".
    [Fact]
    public async Task Generate_TryAddLosesRace_CountsAsSkipped()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(9000m), "SGD"));
        _invoiceRepo.RejectNextAdd = true;

        var result = await CreateService().GenerateForPeriodAsync(2026, 4, default);

        Assert.Equal(0, result.Generated);
        Assert.Equal(1, result.Skipped);
    }

    // An invoice is denominated in the CONFIGURED currency; the `Currency` column on the usage rows it
    // bills is not read back. Seeding a foreign currency is what makes this falsifiable.
    [Fact]
    public async Task Generate_ForeignCurrencyOnUsageRows_InvoiceUsesConfiguredCurrency()
    {
        _usageRepo.Seed(
            UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(9000m), "USD"),
            UsageRecord.CreateCompute(Guid.NewGuid(), "web-02", TenantId, T0, 60, Rate.Of(9000m), "USD"));

        var result = await CreateService().GenerateForPeriodAsync(2026, 4, default);

        Assert.Equal(1, result.Generated);
        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(BillingConstants.DefaultCurrency, invoice.Currency);
    }

    // A resource billed at Cost=0 still gets an invoice item, so the trail exists instead of the
    // resource silently vanishing from the bill.
    [Fact]
    public async Task Generate_ZeroCostResource_StillCreatesItemWithZeroAmount()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(0m), "SGD"));

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        var item = Assert.Single(invoice.Items);
        Assert.Equal(0L, item.Amount);
    }

    // ── titan-ops architecture/money-and-wallet.md#human-confirmed-debit: generation touches NO money ──────────────────────────────
    //
    // An invoice states usage, and the wallet is untouched until someone presses a button
    // (PayFromWalletAsync): generation never offsets or settles from the wallet.

    // Credit sitting in the wallet does not reduce the invoice, and nothing is drawn down.
    [Fact]
    public async Task Generate_TenantHasCredit_InvoiceIsFullUsage_WalletUntouched()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(300_000m), "VND"));
        _walletRepo.SeedBalance(TenantId, 500_000L);

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(300_000L, invoice.Total);                        // full usage, not usage - credit
        Assert.Single(invoice.Items);                                 // one usage line, no Credit line
        Assert.Equal(InvoiceStatus.New, invoice.Status);              // a real payable with a DueAt
        Assert.True(invoice.DueAt > invoice.IssuedAt);
        Assert.Empty(_walletRepo.Debits);                             // nothing was drawn from the wallet
    }

    // A wallet that MORE than covers the period still does not settle the invoice at generation. This
    // fails if generation ever settles from the wallet: the customer would find the bill paid without
    // having agreed to pay it.
    [Fact]
    public async Task Generate_WalletMoreThanCoversUsage_InvoiceStillNew_NotSettled()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(10_000m), "VND"));
        _walletRepo.SeedBalance(TenantId, 900_000L);

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(10_000L, invoice.Total);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Null(invoice.PaidAt);
        Assert.Null(invoice.PaidBy);
        // ...and it ages to Overdue like any other unpaid bill.
        Assert.Equal(InvoiceDisplayStatus.Overdue, invoice.DisplayStatus(invoice.DueAt.AddDays(1)));
    }

    // Promo credit is the customer's too: titan-ops architecture/money-and-wallet.md#human-confirmed-debit makes
    // no distinction between promo and purchased.
    [Fact]
    public async Task Generate_TenantHasPromoCredit_StillNotApplied()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(300_000m), "VND"));
        _walletRepo.SeedBalance(TenantId, 500_000L);

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(300_000L, invoice.Total);
        Assert.Single(invoice.Items);
    }

    // A tenant with an empty wallet is billed identically: there is only ONE generation path, so "has
    // credit" and "has none" cannot diverge.
    [Fact]
    public async Task Generate_NoCredit_SameShapeAsWithCredit()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(300_000m), "VND"));

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(300_000L, invoice.Total);
        Assert.Single(invoice.Items);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    // A zero-total invoice settles itself. Not an exception to
    // "a person must press the button": at zero there is no money to move, so there is nobody to ask.
    // It must NOT be SettledByWallet — the wallet is untouched, and claiming otherwise would put a
    // phantom spend in the wallet statement.
    [Fact]
    public async Task Generate_ZeroTotal_AutoSettles_AsPaidBySystem_NotByWallet()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, "web-01", TenantId, T0, 60, Rate.Of(0m), "VND"));
        _walletRepo.SeedBalance(TenantId, 500_000L);

        await CreateService().GenerateForPeriodAsync(2026, 4, default);

        var invoice = Assert.Single(_invoiceRepo.All);
        Assert.Equal(0L, invoice.Total);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal("system", invoice.PaidBy);
        Assert.Equal(invoice.IssuedAt, invoice.PaidAt);
        // ...and it never ages into a reminder about nothing.
        Assert.Equal(InvoiceDisplayStatus.Paid, invoice.DisplayStatus(invoice.DueAt.AddDays(30)));
        Assert.Empty(_walletRepo.Debits);   // the wallet was not touched
    }

    // A zero-total invoice is terminal on arrival, so the pay button has nothing to act on.
    [Fact]
    public async Task PayFromWallet_ZeroTotalInvoice_AlreadyTerminal_ThrowsInvalidState()
    {
        var invoice = MakeOwedInvoice(TenantId, 0L, T0);
        invoice.AutoSettleZeroTotal();
        _invoiceRepo.Seed(invoice);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_INVALID_STATE", ex.Code);
    }

    // ── GetTenantInvoicesAsync / GetTenantInvoiceAsync ───────────────────

    // A tenant only ever sees its own invoices, with the status derived at read time.
    [Fact]
    public async Task GetTenantInvoices_ReturnsOnlyCallerTenant_WithDerivedStatus()
    {
        var mine = Invoice.Generate(TenantId, 2026, 3, DateTimeOffset.UtcNow.AddDays(-40),
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        var someoneElses = Invoice.Generate(OtherTenantId, 2026, 3, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "other", 5L, "SGD")], "SGD");
        _invoiceRepo.Seed(mine, someoneElses);

        var result = await CreateService().GetTenantInvoicesAsync(default);

        var dto = Assert.Single(result);
        Assert.Equal(mine.Id, dto.Id);
        Assert.Equal(InvoiceDisplayStatus.Overdue, dto.Status); // issued 40 days ago, 7-day term, unpaid
    }

    // An invoice id belonging to another tenant must 404, not leak that it exists.
    [Fact]
    public async Task GetTenantInvoice_OtherTenant_Throws_INVOICE_NOT_FOUND()
    {
        var someoneElses = Invoice.Generate(OtherTenantId, 2026, 3, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "other", 5L, "SGD")], "SGD");
        _invoiceRepo.Seed(someoneElses);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetTenantInvoiceAsync(someoneElses.Id, default));

        Assert.Equal("INVOICE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task GetTenantInvoice_UnknownId_Throws_INVOICE_NOT_FOUND()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetTenantInvoiceAsync(Guid.NewGuid(), default));

        Assert.Equal("INVOICE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task GetTenantInvoice_OwnInvoice_ReturnsItemDetail()
    {
        var invoice = Invoice.Generate(TenantId, 2026, 3, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        _invoiceRepo.Seed(invoice);

        var dto = await CreateService().GetTenantInvoiceAsync(invoice.Id, default);

        Assert.Equal(invoice.Id, dto.Id);
        var item = Assert.Single(dto.Items);
        Assert.Equal("web-01", item.ResourceName);
    }

    // ── ListAllAsync (admin) ──────────────────────────────────────────────

    // Admin list filters by the derived DisplayStatus, e.g. "Overdue" which is never stored.
    [Fact]
    public async Task ListAll_FilterByStatus_Overdue_ReturnsDerivedOverdue()
    {
        var overdue = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow.AddDays(-40),
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        var current = Invoice.Generate(OtherTenantId, 2026, 4, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-02", 5L, "SGD")], "SGD");
        _invoiceRepo.Seed(overdue, current);

        var result = await CreateService().ListAllAsync(InvoiceDisplayStatus.Overdue.ToString(), default);

        var dto = Assert.Single(result);
        Assert.Equal(overdue.Id, dto.Id);
    }

    [Fact]
    public async Task ListAll_NoStatusFilter_ReturnsEverything()
    {
        var a = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow.AddDays(-40),
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        var b = Invoice.Generate(OtherTenantId, 2026, 4, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-02", 5L, "SGD")], "SGD");
        _invoiceRepo.Seed(a, b);

        var result = await CreateService().ListAllAsync(null, default);

        Assert.Equal(2, result.Count);
    }

    // ── MarkPaidAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkPaid_UnknownId_Throws_INVOICE_NOT_FOUND()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().MarkPaidAsync(Guid.NewGuid(), default));

        Assert.Equal("INVOICE_NOT_FOUND", ex.Code);
    }

    // Marking an overdue invoice paid must flip DisplayStatus straight to Paid, not leave it
    // reading Overdue because "now" is still past the original DueAt.
    [Fact]
    public async Task MarkPaid_OverdueInvoice_BecomesPaid()
    {
        var invoice = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow.AddDays(-40),
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        _invoiceRepo.Seed(invoice);

        var dto = await CreateService().MarkPaidAsync(invoice.Id, default);

        Assert.Equal(InvoiceDisplayStatus.Paid, dto.Status);
        Assert.Equal("admin-1", invoice.PaidBy);
    }

    [Fact]
    // A second "Mark paid" of the same outside payment changes nothing — including who
    // is recorded as having marked it.
    public async Task MarkPaid_Repeated_ChangesNothing()
    {
        var invoice = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        invoice.MarkPaid(DateTimeOffset.UtcNow, "admin-0");
        DateTimeOffset? firstPaidAt = invoice.PaidAt;
        _invoiceRepo.Seed(invoice);

        await CreateService().MarkPaidAsync(invoice.Id, default);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal("admin-0", invoice.PaidBy);
        Assert.Equal(firstPaidAt, invoice.PaidAt);
    }

    // A different money route stays refused: recording outside money on an invoice the wallet already
    // paid would count the customer's money twice.
    [Fact]
    public async Task MarkPaid_OnAnInvoiceTheWalletPaid_Throws_INVOICE_ALREADY_PAID()
    {
        var invoice = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        invoice.SettleFromWallet(DateTimeOffset.UtcNow);
        _invoiceRepo.Seed(invoice);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().MarkPaidAsync(invoice.Id, default));

        Assert.Equal("INVOICE_ALREADY_PAID", ex.Code);
    }
    // A wallet debit for the invoice is waiting: the money is about to leave the wallet, so recording outside
    // money now would take it twice. Refused, and the invoice is left as it was.
    [Fact]
    public async Task MarkPaid_WhileAWalletDebitIsPending_Throws_INVOICE_PAYMENT_CONFLICT()
    {
        var invoice = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        _invoiceRepo.Seed(invoice);
        _mutationRepo.Add(WalletMutation.ForDebit(Guid.NewGuid(), 10L, WalletMutationConsumer.InvoiceRefType, invoice.Id));

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().MarkPaidAsync(invoice.Id, default));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Null(invoice.PaidBy);
    }

    // A failed debit moved no money, so the admin may still record the outside payment.
    [Fact]
    public async Task MarkPaid_AfterAWalletDebitFailed_BecomesPaid()
    {
        var invoice = Invoice.Generate(TenantId, 2026, 1, DateTimeOffset.UtcNow,
            [InvoiceItem.Create(UsageResourceType.Compute, VmId, "web-01", 10L, "SGD")], "SGD");
        _invoiceRepo.Seed(invoice);
        var failed = WalletMutation.ForDebit(Guid.NewGuid(), 10L, WalletMutationConsumer.InvoiceRefType, invoice.Id);
        failed.MarkFailed("wallet spent before the debit was applied");
        _mutationRepo.Add(failed);

        await CreateService().MarkPaidAsync(invoice.Id, default);

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    // ── GetReceivablesAsync (titan-ops architecture/money-and-wallet.md#postpaid-no-brake) ──────────────────────────────

    // Overdue is DERIVED (New past DueAt), never stored. This test is the one that fails if someone
    // "optimises" the report into a `WHERE status = 'Overdue'` query — which would return nothing.
    [Fact]
    public async Task GetReceivables_AggregatesPerTenant_SplittingOverdueFromMerelyOutstanding()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var overdue = MakeOwedInvoice(tenantA, 300L, DateTimeOffset.UtcNow.AddDays(-40));  // due 33d ago
        var notYetDue = MakeOwedInvoice(tenantA, 100L, DateTimeOffset.UtcNow.AddDays(-1));
        var otherTenant = MakeOwedInvoice(tenantB, 50L, DateTimeOffset.UtcNow.AddDays(-30));
        var alreadyPaid = MakeOwedInvoice(tenantB, 999L, DateTimeOffset.UtcNow.AddDays(-30));
        alreadyPaid.MarkPaid(DateTimeOffset.UtcNow, "admin-1");
        _invoiceRepo.Seed(overdue, notYetDue, otherTenant, alreadyPaid);
        _tenantRepo.Setup(r => r.ListAllAsync(default)).ReturnsAsync(
            [MakeTenant(tenantA, "acme", debtCeiling: 350L), MakeTenant(tenantB, "globex")]);

        var ar = await CreateService().GetReceivablesAsync();

        Assert.Equal(450m, ar.TotalOutstanding);        // 300 + 100 + 50 — the paid one is not debt
        Assert.Equal(350m, ar.TotalOverdue);            // 300 + 50
        Assert.Equal(2, ar.OverdueInvoiceCount);
        Assert.Equal(2, ar.TenantsInDebt);

        var a = ar.PerTenant.Single(r => r.TenantId == tenantA);
        Assert.Equal("acme", a.TenantName);             // name resolved, not a raw guid
        Assert.Equal(400m, a.Outstanding);
        Assert.Equal(300m, a.OverdueAmount);
        Assert.Equal(1, a.OverdueCount);
        Assert.Equal(2, a.InvoiceCount);
        Assert.Equal(overdue.DueAt, a.OldestDueAt);
        Assert.Equal(tenantA, ar.PerTenant[0].TenantId); // biggest debtor first

        // The whole point of the ceiling: the admin sees debt and ceiling on one line, and a flag
        // when the first is past the second. 400 owed against a 350 ceiling.
        Assert.Equal(350m, a.DebtCeiling);
        Assert.True(a.OverCeiling);

        // globex owes 50 against the default 500.000 — nowhere near it.
        var b = ar.PerTenant.Single(r => r.TenantId == tenantB);
        Assert.Equal(500_000m, b.DebtCeiling);
        Assert.False(b.OverCeiling);
    }

    // Exactly ON the ceiling is not OVER it — an off-by-one here would flag every tenant whose debt
    // happens to land on a round number the admin typed in.
    [Fact]
    public async Task GetReceivables_DebtExactlyOnTheCeiling_IsNotFlagged()
    {
        var tenantId = Guid.NewGuid();
        _invoiceRepo.Seed(MakeOwedInvoice(tenantId, 500L, DateTimeOffset.UtcNow.AddDays(-1)));
        _tenantRepo.Setup(r => r.ListAllAsync(default)).ReturnsAsync(
            [MakeTenant(tenantId, "acme", debtCeiling: 500L)]);

        var ar = await CreateService().GetReceivablesAsync();

        Assert.Equal(500m, ar.PerTenant.Single().DebtCeiling);
        Assert.False(ar.PerTenant.Single().OverCeiling);
    }

    [Fact]
    public async Task GetReceivables_NoDebt_ReturnsZeroes_NotAnError()
    {
        var ar = await CreateService().GetReceivablesAsync();

        Assert.Equal(0m, ar.TotalOutstanding);
        Assert.Empty(ar.PerTenant);
    }

    // A tenant row missing (deleted) must not blow up the whole receivables report — ops still needs
    // the number, and a guid is a worse label than a name but better than a 500.
    [Fact]
    public async Task GetReceivables_UnknownTenant_FallsBackToId_StillReportsTheDebt()
    {
        var ghost = Guid.NewGuid();
        _invoiceRepo.Seed(MakeOwedInvoice(ghost, 10L, DateTimeOffset.UtcNow.AddDays(-1)));

        var ar = await CreateService().GetReceivablesAsync();

        Assert.Equal(10m, ar.TotalOutstanding);
        Assert.Equal(ghost.ToString(), ar.PerTenant.Single().TenantName);
        // No tenant row means no ceiling to compare against — report the debt, flag nothing.
        Assert.Equal(0m, ar.PerTenant.Single().DebtCeiling);
        Assert.False(ar.PerTenant.Single().OverCeiling);
    }

    // ── PayFromWalletAsync / SettleFromWalletAsAdminAsync (titan-ops architecture/money-and-wallet.md#human-confirmed-debit) ───────
    //
    // A wallet is only ever debited by a person pressing a button — the tenant or an admin on their
    // behalf. Both go through the same path, so these tests assert the enqueued debit and the invoice
    // state; the real apply on Postgres is proven by InvoicePaymentAsyncDbTests and DebtSettlementDbTests.

    // titan-ops architecture/money-and-wallet.md#single-writer-outbox — enough credit: the request ACCEPTS the
    // payment and writes a debit mutation. It does not move money.
    [Fact]
    public async Task PayFromWallet_EnoughCredit_EnqueuesDebitMutation_MovesNoMoneyYet()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);

        var dto = await CreateService().PayFromWalletAsync(invoice.Id, default);

        // One mutation, carrying the exact invoice total and pointing back at the invoice that raised it.
        var mutation = Assert.Single(_mutationRepo.Added);
        Assert.Equal(WalletMutationType.Debit, mutation.Type);
        Assert.Equal(300_000L, mutation.Amount);
        Assert.Equal("Invoice", mutation.RefType);
        Assert.Equal(invoice.Id, mutation.RefId);
        Assert.Equal(WalletMutationStatus.Pending, mutation.Status);

        // Nothing has moved: the invoice is still outstanding and no debit was applied to the wallet.
        // This is the assertion that would have to change if anyone put the debit back in the request.
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Null(invoice.PaidAt);
        Assert.Null(invoice.PaidBy);
        Assert.Empty(_walletRepo.Debits);

        // ...and the caller can tell "accepted, in flight" apart from "nobody has paid this".
        Assert.Equal(PaymentState.Processing.ToString(), dto.PaymentState);

        // The notify fires after the commit, naming the mutation the consumer must apply.
        Assert.Equal(mutation.Id, Assert.Single(_walletBus.Published).MutationId);
    }

    // The bus is best-effort, the mutation is not. A publish that blows up must not turn an accepted
    // payment into an error: the row is durable and the sweeper will drive it.
    [Fact]
    public async Task PayFromWallet_PublishFails_StillAcceptsThePayment()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        _walletBus.ThrowOnPublish = true;

        var dto = await CreateService().PayFromWalletAsync(invoice.Id, default);

        Assert.Single(_mutationRepo.Added);
        Assert.Equal(PaymentState.Processing.ToString(), dto.PaymentState);
    }

    // Not enough credit: NOTHING changes and the error names the shortfall. There is no partial payment,
    // so "nothing changes" is the only correct outcome here.
    [Fact]
    public async Task PayFromWallet_NotEnoughCredit_ThrowsAndChangesNothing()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 100_000L);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        Assert.Contains("200000", ex.Message.Replace(".", "").Replace(",", ""));
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Null(invoice.PaidAt);
        Assert.Empty(_walletRepo.Debits);
    }

    // An already-settled invoice is never paid twice. Only a DIFFERENT money route is refused — money
    // that arrived outside Titan (MarkPaid) must not be answered as "your wallet paid this". The same
    // route is a repeat (below).
    [Fact]
    public async Task PayFromWallet_AlreadyPaidOutsideTitan_ThrowsInvalidState()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        invoice.MarkPaid(T0.AddDays(1), "admin-1");
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_INVALID_STATE", ex.Code);
        Assert.Empty(_walletRepo.Debits);
    }

    // Another tenant's invoice is indistinguishable from one that does not exist: telling
    // them apart would confirm the id exists (same no-leak rule as GetTenantInvoiceAsync).
    [Fact]
    public async Task PayFromWallet_OtherTenantsInvoice_ThrowsNotFound()
    {
        var invoice = MakeOwedInvoice(OtherTenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_NOT_FOUND", ex.Code);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    // Overdue never blocks payment. Overdue is New past DueAt, so
    // this is the same New invoice with a clock that moved, and it must still be payable.
    [Fact]
    public async Task PayFromWallet_OverdueInvoice_IsStillAccepted()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, DateTimeOffset.UtcNow.AddDays(-90));
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);

        Assert.Equal(InvoiceDisplayStatus.Overdue, invoice.DisplayStatus(DateTimeOffset.UtcNow));

        await CreateService().PayFromWalletAsync(invoice.Id, default);

        // Accepted, same as any other outstanding invoice: being late changes nothing about whether the
        // payment may be made (titan-ops architecture/money-and-wallet.md#postpaid-no-brake — overdue reminds, it does not enforce).
        Assert.Single(_mutationRepo.Added);
    }

    // An empty wallet is refused like any other shortfall, not treated as a zero-amount payment.
    [Fact]
    public async Task PayFromWallet_EmptyWallet_ThrowsAndPlansNothing()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Empty(_walletRepo.Debits);
    }

    // One đồng short is still short: payment is all-or-nothing, not "as much as fits".
    [Fact]
    public async Task PayFromWallet_OneUnitShort_ThrowsAndChangesNothing()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 299_999L);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    // ── A repeat of the same money route answers like the first (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat) ──

    [Fact]
    public async Task PayFromWallet_InvoiceTheWalletAlreadyPaid_ChangesNothing()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        invoice.SettleFromWallet(T0.AddDays(1));
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);

        await CreateService().PayFromWalletAsync(invoice.Id, default);

        Assert.Equal(InvoiceStatus.SettledByWallet, invoice.Status);
        Assert.Empty(_mutationRepo.Added);
        Assert.Empty(_walletRepo.Debits);
    }

    [Fact]
    public async Task PayFromWallet_WhileItsDebitIsWaiting_ChangesNothing()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        await CreateService().PayFromWalletAsync(invoice.Id, default); // the first press: one debit waiting
        Assert.Single(_mutationRepo.Added);

        await CreateService().PayFromWalletAsync(invoice.Id, default); // the second press

        Assert.Single(_mutationRepo.Added);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    // A FAILED debit is not a waiting one: nobody is paying this invoice, so a new press is not a repeat. It
    // reopens the invoice's one debit row instead of adding a second
    // (titan-ops features/billing/invoice/design.md#retry-after-failed-debit).
    [Fact]
    public async Task PayFromWallet_AfterADebitFailed_ReopensTheSameRow()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        await CreateService().PayFromWalletAsync(invoice.Id, default);
        WalletMutation first = Assert.Single(_mutationRepo.Added);
        first.MarkFailed("insufficient funds at apply time");

        await CreateService().PayFromWalletAsync(invoice.Id, default);

        WalletMutation only = Assert.Single(_mutationRepo.Added);
        Assert.Same(first, only);
        Assert.Equal(WalletMutationStatus.Pending, only.Status);
        Assert.Empty(_walletRepo.Debits);
    }

    // The failed row must ask for this invoice's money from this tenant's wallet; anything else is not the
    // payment to reopen, and nothing changes.
    [Fact]
    public async Task PayFromWallet_FailedDebitForADifferentAmount_IsAConflict_AndStaysFailed()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        await CreateService().PayFromWalletAsync(invoice.Id, default);
        WalletMutation first = Assert.Single(_mutationRepo.Added);
        first.MarkFailed("insufficient funds at apply time");
        var stale = WalletMutation.ForDebit(first.WalletId, 299_000L, WalletMutationConsumer.InvoiceRefType, invoice.Id);
        stale.MarkFailed("insufficient funds at apply time");
        _mutationRepo.ByRef[(WalletMutationConsumer.InvoiceRefType, invoice.Id)] = stale;

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        Assert.Equal(WalletMutationStatus.Failed, stale.Status);
    }

    [Fact]
    public async Task PayFromWallet_FailedDebitOnAnotherWallet_IsAConflict_AndStaysFailed()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        var foreign = WalletMutation.ForDebit(Guid.NewGuid(), 300_000L, WalletMutationConsumer.InvoiceRefType, invoice.Id);
        foreign.MarkFailed("insufficient funds at apply time");
        _mutationRepo.ByRef[(WalletMutationConsumer.InvoiceRefType, invoice.Id)] = foreign;

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        Assert.Equal(WalletMutationStatus.Failed, foreign.Status);
    }

    [Fact]
    public async Task SettleFromWallet_InvoiceTheWalletAlreadyPaid_ChangesNothing()
    {
        var invoice = MakeOwedInvoice(OtherTenantId, 300_000L, T0);
        invoice.SettleFromWallet(T0.AddDays(1));
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(OtherTenantId, 500_000L);

        await CreateService().SettleFromWalletAsAdminAsync(invoice.Id, default);

        Assert.Empty(_mutationRepo.Added);
    }

    // Two presses at the same moment both pass the check; the second collides at UNIQUE(ref_type,
    // ref_id). If what it collided with is the OTHER press's debit, that is a repeat.
    [Fact]
    public async Task PayFromWallet_CollidingWithAnotherPressOfTheSameButton_Succeeds()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        WalletMutation winner = WalletMutation.ForDebit(Guid.NewGuid(), 300_000L, WalletMutationConsumer.InvoiceRefType, invoice.Id);
        _walletRepo.RejectNextSave = true;
        _walletRepo.WhenRejecting = () => _mutationRepo.ByRef[(WalletMutationConsumer.InvoiceRefType, invoice.Id)] = winner;

        await CreateService().PayFromWalletAsync(invoice.Id, default);

        Assert.Empty(_walletRepo.Debits);
    }

    // Losing the save to a conflict that is NOT another press of this button must surface as a
    // conflict, not as a silent success on a stale balance.
    [Fact]
    public async Task PayFromWallet_LostSettleRace_ThrowsConflict()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(TenantId, 500_000L);
        _walletRepo.RejectNextSave = true;   // simulate losing the optimistic-concurrency version check

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().PayFromWalletAsync(invoice.Id, default));

        Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
    }

    // The admin path reaches the same outcome for a tenant that is NOT the caller's own:
    // an admin pressing the button on someone's behalf, never a tenant-scoped read. Under titan-ops architecture/money-and-wallet.md#single-writer-outbox that
    // shared outcome is an ENQUEUED mutation, which is the point of both buttons sharing one method —
    // neither can drift into debiting synchronously while the other queues.
    [Fact]
    public async Task SettleFromWalletAsAdmin_OtherTenant_EnqueuesDebitForThatTenant()
    {
        var invoice = MakeOwedInvoice(OtherTenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(OtherTenantId, 500_000L);

        var dto = await CreateService().SettleFromWalletAsAdminAsync(invoice.Id, default);

        var mutation = Assert.Single(_mutationRepo.Added);
        Assert.Equal(WalletMutationType.Debit, mutation.Type);
        Assert.Equal(300_000L, mutation.Amount);
        Assert.Equal(invoice.Id, mutation.RefId);

        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Empty(_walletRepo.Debits);
        Assert.Equal(PaymentState.Processing.ToString(), dto.PaymentState);
    }

    // An admin spends a tenant's money on the tenant's behalf: that leaves an audit row naming who pressed
    // it and whose wallet it was (titan-ops architecture/tenancy-and-admin.md#admin-audit).
    [Fact]
    public async Task SettleFromWalletAsAdmin_WritesOneAuditRow_ActorIsTheAdmin_OwnerIsTheInvoiceTenant()
    {
        var invoice = MakeOwedInvoice(OtherTenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(OtherTenantId, 500_000L);

        await CreateService().SettleFromWalletAsAdminAsync(invoice.Id, default);

        var row = Assert.Single(_auditLog.Staged);
        Assert.Equal("invoice.settle_from_wallet", row.Action);
        Assert.Equal("admin-1", row.ActorKeycloakUserId);
        Assert.Equal("invoice", row.ResourceType);
        Assert.Equal(invoice.Id, row.ResourceId);
        Assert.Equal(OtherTenantId, row.OwnerTenantId);
    }

    // A repeat spends nothing, so it records nothing.
    [Fact]
    public async Task SettleFromWalletAsAdmin_Repeat_WritesNoAuditRow()
    {
        var invoice = MakeOwedInvoice(OtherTenantId, 300_000L, T0);
        invoice.SettleFromWallet(T0.AddDays(1));
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(OtherTenantId, 500_000L);

        await CreateService().SettleFromWalletAsAdminAsync(invoice.Id, default);

        Assert.Empty(_auditLog.Staged);
    }

    // The admin path is no way around all-or-nothing: an underfunded wallet fails the same way.
    [Fact]
    public async Task SettleFromWalletAsAdmin_NotEnoughCredit_ThrowsAndChangesNothing()
    {
        var invoice = MakeOwedInvoice(OtherTenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _walletRepo.SeedBalance(OtherTenantId, 100_000L);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().SettleFromWalletAsAdminAsync(invoice.Id, default));

        Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        Assert.Equal(InvoiceStatus.New, invoice.Status);
    }

    // A missing invoice on the admin path is a plain 404 — there is no tenant to leak anything to.
    [Fact]
    public async Task SettleFromWalletAsAdmin_MissingInvoice_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().SettleFromWalletAsAdminAsync(Guid.NewGuid(), default));

        Assert.Equal("INVOICE_NOT_FOUND", ex.Code);
    }

    private static Invoice MakeOwedInvoice(Guid tenantId, long amount, DateTimeOffset issuedAt)
    {
        var items = new List<InvoiceItem>
        {
            InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-01", amount, "VND"),
        };
        return Invoice.Generate(tenantId, issuedAt.Year, issuedAt.Month, issuedAt, items, "VND");
    }

    private static Titan.Domain.Aggregates.Platform.Tenant.Tenant MakeTenant(
        Guid id, string name, long? debtCeiling = null)
    {
        var tenant = TestTenant.Active(name, $"owner-{name}", $"{name}@x.com");
        if (debtCeiling.HasValue)
            tenant.UpdateDebtCeiling(debtCeiling.Value);
        typeof(Titan.Domain.Aggregates.Platform.Tenant.Tenant)
            .GetProperty(nameof(Titan.Domain.Aggregates.Platform.Tenant.Tenant.Id))!.SetValue(tenant, id);
        return tenant;
    }

    // ── The payment state an invoice shows its owner (titan-ops architecture/money-and-wallet.md#single-writer-outbox) ──
    //
    // The customer's question is "did my payment go through?", and under an async debit the invoice's
    // own Status cannot answer it: a New invoice might be one nobody has paid, or one whose payment is
    // in flight, or one whose payment failed. PaymentState is what tells those three apart.

    [Theory]
    [InlineData(WalletMutationStatus.Pending, "Processing")]
    [InlineData(WalletMutationStatus.Applied, "Settled")]
    [InlineData(WalletMutationStatus.Failed, "Failed")]
    public async Task GetTenantInvoice_ShowsThePaymentStateOfItsMutation(
        WalletMutationStatus status, string expected)
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _mutationRepo.ByRef[("Invoice", invoice.Id)] = MutationInState(invoice.Id, status);

        var dto = await CreateService().GetTenantInvoiceAsync(invoice.Id, default);

        Assert.Equal(expected, dto.PaymentState);
    }

    [Fact] // no mutation means nobody ever pressed pay — not "processing", not "failed"
    public async Task GetTenantInvoice_NeverPaid_HasNoPaymentState()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);

        var dto = await CreateService().GetTenantInvoiceAsync(invoice.Id, default);

        Assert.Null(dto.PaymentState);
    }

    [Fact] // the LIST view carries it too, and reads every invoice's state in one go
    public async Task GetTenantInvoices_CarryTheirOwnPaymentStates()
    {
        var paying = MakeOwedInvoice(TenantId, 300_000L, T0);
        var untouched = MakeOwedInvoice(TenantId, 100_000L, T0.AddMonths(-1));
        _invoiceRepo.Seed(paying);
        _invoiceRepo.Seed(untouched);
        _mutationRepo.ByRef[("Invoice", paying.Id)] =
            MutationInState(paying.Id, WalletMutationStatus.Pending);

        var dtos = await CreateService().GetTenantInvoicesAsync(default);

        Assert.Equal("Processing", dtos.Single(d => d.Id == paying.Id).PaymentState);
        Assert.Null(dtos.Single(d => d.Id == untouched.Id).PaymentState);
    }

    // The ADMIN list carries it too. A read path that never asks for the mutation status is invisible to
    // any audit of the status enum's readers; only a test like this catches it.
    [Fact]
    public async Task ListAll_CarriesPaymentState_ForAdmins()
    {
        var invoice = MakeOwedInvoice(TenantId, 300_000L, T0);
        _invoiceRepo.Seed(invoice);
        _mutationRepo.ByRef[("Invoice", invoice.Id)] =
            MutationInState(invoice.Id, WalletMutationStatus.Pending);

        var dtos = await CreateService().ListAllAsync(null, default);

        Assert.Equal("Processing", dtos.Single(d => d.Id == invoice.Id).PaymentState);
    }

    private static WalletMutation MutationInState(Guid invoiceId, WalletMutationStatus status)
    {
        var mutation = WalletMutation.ForDebit(Guid.NewGuid(), 300_000L, "Invoice", invoiceId);
        if (status == WalletMutationStatus.Applied) mutation.MarkApplied();
        if (status == WalletMutationStatus.Failed) mutation.MarkFailed("not enough money");
        return mutation;
    }
}


// ── Fakes ─────────────────────────────────────────────────────────────────

public class FakeInvoiceRepository : IInvoiceRepository
{
    public List<Invoice> All { get; } = [];
    public int TryAddCallCount { get; private set; }

    /// Simulates the unique index on (TenantId, PeriodYear, PeriodMonth) refusing a concurrent
    /// second insert for the same period.
    public bool RejectNextAdd { get; set; }

    public void Seed(params Invoice[] invoices) => All.AddRange(invoices);

    /// Records a period as already billed — used by the idempotent-generation test, where what matters
    /// is that ExistsForPeriodAsync returns true.
    public void SeedExistingPeriod(Guid tenantId, int year, int month) =>
        All.Add(Invoice.Generate(tenantId, year, month, DateTimeOffset.UtcNow, [], "SGD"));

    public Task<bool> TryAddAsync(Invoice invoice, CancellationToken ct)
    {
        TryAddCallCount++;
        if (RejectNextAdd) { RejectNextAdd = false; return Task.FromResult(false); }

        All.Add(invoice);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsForPeriodAsync(Guid tenantId, int year, int month, CancellationToken ct) =>
        Task.FromResult(All.Any(i => i.TenantId == tenantId && i.PeriodYear == year && i.PeriodMonth == month));

    public Task<IReadOnlyList<Invoice>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Invoice>>(All.Where(i => i.TenantId == tenantId).ToList());

    public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(All.FirstOrDefault(i => i.Id == id));

    // In-memory, so "tracked" is the same instance — mutating it via MarkPaid is already visible
    // to every other reference without a separate save step.
    public Task<Invoice?> GetForUpdateAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(All.FirstOrDefault(i => i.Id == id));

    // titan-ops architecture/money-and-wallet.md#postpaid-no-brake — outstanding = Status New only, oldest first. Deliberately NOT filtered on the string
    // "Overdue": that is derived at read time and stored nowhere, so filtering on it returns nothing.
    public Task<IReadOnlyList<Invoice>> ListOutstandingByTenantAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Invoice>>(All
            .Where(i => i.TenantId == tenantId && i.Status == InvoiceStatus.New)
            .OrderBy(i => i.IssuedAt)
            .ToList());

    public Task<IReadOnlyList<Invoice>> ListOutstandingAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Invoice>>(All
            .Where(i => i.Status == InvoiceStatus.New)
            .OrderBy(i => i.IssuedAt)
            .ToList());

    public Task<IReadOnlyList<Invoice>> ListAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Invoice>>(All.ToList());

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

/// Wallet fake: GetByTenantForUpdateAsync hands back a real Wallet holding the seeded balance. Debits are
/// recorded at COMMIT (not when staged), so a simulated version conflict (RejectNextSave) leaves the
/// Debits list empty — the same "nothing was charged" a rolled-back transaction gives.
public class FakeWalletRepository : IWalletRepository
{
    private readonly Dictionary<Guid, Wallet> _wallets = [];
    private readonly List<WalletTransaction> _staged = [];

    /// Debits COMMITTED so far — the assertion surface for "the wallet was untouched".
    public List<(Guid TenantId, decimal Amount, Guid RefId)> Debits { get; } = [];

    /// Simulate losing the optimistic-concurrency version check at SaveChanges — nothing commits.
    public bool RejectNextSave { get; set; }

    /// Runs as the rejection happens — lets a test put the concurrent winner's row in place.
    public Action? WhenRejecting { get; set; }

    public void SeedBalance(Guid tenantId, long amount)
    {
        var wallet = Wallet.Open(tenantId, "VND");
        if (amount > 0) wallet.Credit(amount);   // set the balance through the aggregate
        _wallets[tenantId] = wallet;
    }

    public Task<long> GetBalanceAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(_wallets.TryGetValue(tenantId, out var w) ? w.AvailableAmount : 0L);

    public Task<Wallet?> GetByTenantForUpdateAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(_wallets.TryGetValue(tenantId, out var w) ? w : null);

    // The consumer loads by wallet id; the invoice handlers do not, but the fake honours it faithfully.
    public Task<Wallet?> GetByIdForUpdateAsync(Guid walletId, CancellationToken ct) =>
        Task.FromResult(_wallets.Values.FirstOrDefault(w => w.Id == walletId));

    public void Add(Wallet wallet) => _wallets[wallet.TenantId] = wallet;

    public void AddTransaction(WalletTransaction transaction) => _staged.Add(transaction);

    public Task<IReadOnlyList<WalletTransaction>> ListTransactionsByTenantAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WalletTransaction>>([]);

    public Task SaveChangesAsync(CancellationToken ct)
    {
        if (RejectNextSave)
        {
            RejectNextSave = false;
            _staged.Clear();   // rolled back — nothing committed
            if (WhenRejecting != null)
            {
                WhenRejecting();
            }
            throw new ConcurrencyConflictException("WALLET_CONCURRENCY_CONFLICT", "simulated conflict");
        }

        // Commit: record the InvoicePayment debits (Amount is stored negative by ForInvoicePayment).
        foreach (var t in _staged.Where(x => x.Type == WalletTransactionType.InvoicePayment))
        {
            var tenantId = _wallets.First(kv => kv.Value.Id == t.WalletId).Key;
            Debits.Add((tenantId, -t.Amount, t.RefId));
        }
        _staged.Clear();
        return Task.CompletedTask;
    }}
