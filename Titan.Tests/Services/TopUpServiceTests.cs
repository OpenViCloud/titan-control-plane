using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Billing.TopUp;
using Titan.Application.Commands.Billing.TopUp;
using Titan.Application.Commands.Billing.TopUp.Contracts;
using Titan.Application.Queries.Billing.TopUp;
using Titan.Application.Queries.Billing.TopUp.Contracts;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

// The TopUp lifecycle: Request (tenant, Pending), Confirm (real money in), Grant (admin promo, born
// Confirmed), and the list reads.
//
// Confirm does not credit the wallet in the request (titan-ops architecture/money-and-wallet.md#single-writer-outbox):
// it writes ONE credit WalletMutation in the same save as the Confirmed status, then publishes a
// post-commit notify; the single-writer consumer credits the wallet later. Grant credits synchronously.
//
// Real aggregates over mocked repositories; a version/unique conflict is simulated by making
// SaveChangesAsync throw. Atomicity and the mutation UNIQUE are proven on Postgres
// (TopUpConfirmDbTests).
public class TopUpServiceTests
{
    private readonly Mock<ICreditRepository> _repo = new();
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<IWalletMutationRepository> _mutationRepo = new();
    private readonly Mock<IWalletMutationPublisher> _bus = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly CreditOptions _options = new() { MaxGrantAmount = 100_000_000m, Currency = "VND" };
    private static readonly Guid Tenant = Guid.NewGuid();

    public TopUpServiceTests()
    {
        // A successful save is the default; tests exercising a conflict override this to throw.
        _walletRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _bus.Setup(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("admin-1");
    }

    // Facade over the TopUp handlers and the shared TopUpConfirmation rule; every method forwards to
    // exactly one target and adds nothing. ConfirmAsync goes to the RULE rather than the admin handler
    // because these tests drive both channels (SePay and Manual); the admin handler's own job — stamping
    // Manual and the caller — has its own test below.
    private TopUpFacade CreateService() =>
        new(_repo.Object, _walletRepo.Object, _mutationRepo.Object, _bus.Object, _currentUser.Object, _options);

    private sealed class TopUpFacade(
        ICreditRepository repo,
        IWalletRepository walletRepo,
        IWalletMutationRepository mutationRepo,
        IWalletMutationPublisher bus,
        ICurrentUser currentUser,
        CreditOptions options)
    {
        public Task<RequestTopUpCommandResult> RequestAsync(decimal amount, CancellationToken ct = default)
            => new RequestTopUpCommandHandler(repo, currentUser, options).HandleAsync(new RequestTopUpCommand(amount), ct);

        public Task ConfirmAsync(
            Guid topUpId, TopUpSource source, string confirmedBy, string? paymentTxnRef,
            DateTimeOffset? confirmedAt = null, CancellationToken ct = default)
            => TopUpConfirmation.ConfirmAsync(
                repo, walletRepo, mutationRepo, bus, topUpId, source, confirmedBy, paymentTxnRef, confirmedAt, ct);

        public Task<GrantTopUpCommandResult> GrantAsync(Guid tenantId, long amount, string? reason, CancellationToken ct = default)
            => new GrantTopUpCommandHandler(repo, walletRepo, currentUser, options)
                .HandleAsync(new GrantTopUpCommand(tenantId, amount, reason), ct);

        public Task<IReadOnlyList<TopUpQueryResult>> ListMineAsync(CancellationToken ct = default)
            => new ListMyTopUpsQueryHandler(repo, currentUser).HandleAsync(new ListMyTopUpsQuery(), ct);

        public Task<IReadOnlyList<TopUpQueryResult>> ListAsync(TopUpSource? source, CancellationToken ct = default)
            => new ListTopUpsQueryHandler(repo).HandleAsync(new ListTopUpsQuery(source), ct);
    }
    private void AsTenant() => _currentUser.SetupGet(u => u.TenantId).Returns(Tenant);

    // ── RequestAsync (tenant starts a purchase) ───────────────────────────

    // A tenant's request stages a Pending TopUp in the configured currency with a PaymentRef.
    [Fact]
    public async Task Request_StartsPending_WithConfiguredCurrency_AndPaymentRef()
    {
        AsTenant();
        TopUp? added = null;
        _repo.Setup(r => r.AddTopUp(It.IsAny<TopUp>())).Callback<TopUp>(t => added = t);

        var dto = await CreateService().RequestAsync(100m);

        Assert.NotNull(added);
        Assert.Equal(Tenant, added!.TenantId);
        Assert.Equal(100L, added.Amount);
        Assert.Equal("VND", added.Currency);
        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so status
        // and the payment reference the customer transfers against are asserted on the persisted TopUp.
        Assert.Equal(added.Id, dto.TopUpId);
        Assert.Equal(TopUpStatus.Pending, added.Status);            // no credit until confirmed
        Assert.StartsWith("TITAN-", added.PaymentRef);
        _repo.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    // ── ConfirmAsync (real money in) ──────────────────────────────────────

    // Confirming a Pending TopUp marks it confirmed and ENQUEUES one credit mutation (ref
    // TopUp/topUp.Id, amount = the purchase) in the same save, WITHOUT touching the wallet balance or the
    // ledger. Then it publishes a post-commit notify naming that mutation. The wallet is only READ (to name
    // it in the mutation), never credited here — that is the consumer's job.
    [Fact]
    public async Task Confirm_Pending_EnqueuesCreditMutation_MarksConfirmed_CreditsNothing_Notifies()
    {
        var topUp = TopUp.Create(Tenant, 100L, "VND");
        var wallet = Wallet.Open(Tenant, "VND");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(topUp.Id, default)).ReturnsAsync(topUp);
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(wallet);

        WalletMutation? mutation = null;
        _mutationRepo.Setup(r => r.Add(It.IsAny<WalletMutation>()))
            .Callback<WalletMutation>(c => mutation = c);

        await CreateService().ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin-1", "BANK-9");

        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
        Assert.NotNull(mutation);
        Assert.Equal(WalletMutationType.Credit, mutation!.Type);     // real money → credit mutation
        Assert.Equal(100L, mutation.Amount);
        Assert.Equal(wallet.Id, mutation.WalletId);
        Assert.Equal("TopUp", mutation.RefType);
        Assert.Equal(topUp.Id, mutation.RefId);                      // the UNIQUE key that dedupes retries
        Assert.Equal(WalletMutationStatus.Pending, mutation.Status);

        // Nothing credited synchronously: the consumer is the single writer of the balance.
        Assert.Equal(0L, wallet.AvailableAmount);
        _walletRepo.Verify(r => r.AddTransaction(It.IsAny<WalletTransaction>()), Times.Never);

        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Once);  // one tx: Confirmed + mutation
        _bus.Verify(b => b.PublishAsync(
            It.Is<WalletMutationNotification>(n => n.MutationId == mutation.Id && n.WalletId == wallet.Id),
            It.IsAny<CancellationToken>()), Times.Once);
        // titan-ops architecture/http-contract.md#commands-return-no-data: confirm returns nothing; the confirmation lives on the aggregate a read-back reports.
        Assert.NotNull(topUp.ConfirmedAt);
    }

    // titan-ops architecture/money-and-wallet.md#single-writer-outbox — a bus publish failure must NOT fail the
    // confirm: the credit mutation is already committed, and the sweeper re-drives it.
    [Fact]
    public async Task Confirm_BusPublishThrows_StillConfirms_MutationAlreadyDurable()
    {
        var topUp = TopUp.Create(Tenant, 100L, "VND");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(topUp.Id, default)).ReturnsAsync(topUp);
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(Wallet.Open(Tenant, "VND"));
        _bus.Setup(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bus down"));

        await CreateService().ConfirmAsync(topUp.Id, TopUpSource.SePay, "sys", "txn-1");   // does not throw

        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);   // confirm survived the bus failure
        _mutationRepo.Verify(r => r.Add(It.IsAny<WalletMutation>()), Times.Once);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    // A provisioned tenant always has a wallet; a missing one is a broken invariant, so
    // confirm refuses rather than opening lazily. Nothing enqueued, credited, saved or notified.
    [Fact]
    public async Task Confirm_NoWallet_ThrowsWalletNotFound_CreditsNothing()
    {
        var topUp = TopUp.Create(Tenant, 250L, "VND");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(topUp.Id, default)).ReturnsAsync(topUp);
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync((Wallet?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().ConfirmAsync(topUp.Id, TopUpSource.SePay, "sys", "txn-1"));

        Assert.Equal("WALLET_NOT_FOUND", ex.Code);
        _mutationRepo.Verify(r => r.Add(It.IsAny<WalletMutation>()), Times.Never);
        _walletRepo.Verify(r => r.AddTransaction(It.IsAny<WalletTransaction>()), Times.Never);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Never);
        _bus.Verify(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Confirming an already-confirmed TopUp succeeds, enqueues no new mutation and notifies nothing, so a
    // double webhook or double click is safe. A promo TopUp is born Confirmed, so /confirm on it is a no-op.
    [Fact]
    public async Task Confirm_AlreadyConfirmed_IsIdempotent_NoNewMutation()
    {
        var topUp = TopUp.Create(Tenant, 100L, "VND");
        topUp.Confirm(TopUpSource.SePay, "sys", "txn-1");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(topUp.Id, default)).ReturnsAsync(topUp);

        await CreateService().ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin", "txn-2");

        Assert.Equal(TopUpSource.SePay, topUp.Source);   // not re-stamped by the retry
        _walletRepo.Verify(r => r.GetByTenantForUpdateAsync(It.IsAny<Guid>(), default), Times.Never);
        _mutationRepo.Verify(r => r.Add(It.IsAny<WalletMutation>()), Times.Never);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Never);
        _bus.Verify(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // A concurrent confirm of THIS purchase won the mutation's UNIQUE(ref_type, ref_id): our SaveChanges
    // surfaces the violation as ConcurrencyConflictException. The loser re-reads, sees the WINNER's
    // confirmation and treats the confirm as done (the consumer credits that one mutation exactly once),
    // and does NOT publish a notify for a mutation that never committed.
    // Confirm_LostRace_WinnerNotConfirmed_ThrowsConflict pins the other branch.
    [Fact]
    public async Task Confirm_LostRace_SucceedsOnTheWinnersConfirmation_DoesNotNotify()
    {
        var mine = TopUp.Create(Tenant, 100L, "VND");
        var winner = Reload(mine, TopUpSource.SePay, "sepay", "txn-WINNER");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(mine.Id, default)).ReturnsAsync(mine);
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(Wallet.Open(Tenant, "VND"));
        _walletRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyConflictException("WALLET_MUTATION_DUPLICATE", "lost"));
        _repo.Setup(r => r.GetTopUpAsync(mine.Id, default)).ReturnsAsync(winner);

        await CreateService().ConfirmAsync(mine.Id, TopUpSource.Manual, "admin", "txn-MINE");   // no throw

        _repo.Verify(r => r.GetTopUpAsync(mine.Id, default), Times.Once);   // the winner decided it
        _bus.Verify(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);   // nothing committed on this path → nothing to notify
    }

    // The other half of the lost race: the re-read does NOT show a confirmation, so this was some other
    // operation's conflict — surfaced for retry, nothing written, nothing notified.
    [Fact]
    public async Task Confirm_LostRace_WinnerNotConfirmed_ThrowsConflict()
    {
        var mine = TopUp.Create(Tenant, 100L, "VND");
        var stillPending = TopUp.Create(Tenant, 100L, "VND");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(mine.Id, default)).ReturnsAsync(mine);
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(Wallet.Open(Tenant, "VND"));
        _walletRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyConflictException("WALLET_MUTATION_DUPLICATE", "lost"));
        _repo.Setup(r => r.GetTopUpAsync(mine.Id, default)).ReturnsAsync(stillPending);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().ConfirmAsync(mine.Id, TopUpSource.Manual, "admin", "txn-MINE"));

        Assert.Equal("TOPUP_CONFIRM_CONFLICT", ex.Code);
        _bus.Verify(b => b.PublishAsync(It.IsAny<WalletMutationNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // The admin handler's own job: it confirms as Manual, in the name of the admin who pressed the
    // button. SePay never reaches this handler.
    [Fact]
    public async Task ConfirmTopUpHandler_ConfirmsAsManual_InTheCallersName()
    {
        var topUp = TopUp.Create(Tenant, 100L, "VND");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(topUp.Id, default)).ReturnsAsync(topUp);
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(Wallet.Open(Tenant, "VND"));

        await new ConfirmTopUpCommandHandler(_repo.Object, _walletRepo.Object, _mutationRepo.Object, _bus.Object, _currentUser.Object)
            .HandleAsync(new ConfirmTopUpCommand(topUp.Id, "BANK-7"));

        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
        Assert.Equal(TopUpSource.Manual, topUp.Source);
        Assert.Equal("admin-1", topUp.ConfirmedBy);
    }

    [Fact]
    public async Task Confirm_NotFound_ThrowsTopUpNotFound()
    {
        _repo.Setup(r => r.GetTopUpForUpdateAsync(It.IsAny<Guid>(), default)).ReturnsAsync((TopUp?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().ConfirmAsync(Guid.NewGuid(), TopUpSource.Manual, "admin", null));
        Assert.Equal("TOPUP_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Confirm_FailedTopUp_ThrowsInvalidState()
    {
        var topUp = TopUp.Create(Tenant, 100L, "VND");
        topUp.Fail("declined");
        _repo.Setup(r => r.GetTopUpForUpdateAsync(topUp.Id, default)).ReturnsAsync(topUp);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin", null));
        Assert.Equal("TOPUP_INVALID_STATE", ex.Code);
    }

    // ── GrantAsync (admin promo) ──────────────────────────────────────────

    // A grant credits the wallet with a Promo movement and stages a promo TopUp (Source=PromotionalCredit,
    // the reason, the granting admin), in ONE save.
    [Fact]
    public async Task Grant_CreditsWallet_RecordsPromo_AndPromoTopUp_InOneSave()
    {
        var wallet = Wallet.Open(Tenant, "VND");
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(wallet);
        TopUp? added = null;
        _repo.Setup(r => r.AddTopUp(It.IsAny<TopUp>())).Callback<TopUp>(t => added = t);
        WalletTransaction? ledger = null;
        _walletRepo.Setup(r => r.AddTransaction(It.IsAny<WalletTransaction>()))
                   .Callback<WalletTransaction>(t => ledger = t);

        var dto = await CreateService().GrantAsync(Tenant, 500_000L, "welcome");

        Assert.NotNull(added);
        Assert.Equal(TopUpStatus.Confirmed, added!.Status);
        Assert.Equal(TopUpSource.PromotionalCredit, added.Source);
        Assert.Equal(500_000L, wallet.AvailableAmount);
        Assert.NotNull(ledger);
        Assert.Equal(WalletTransactionType.Promo, ledger!.Type);       // free money → Promo ledger
        Assert.Equal(added.Id, ledger.RefId);
        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so the
        // grant's own fields are asserted on the persisted TopUp.
        Assert.Equal(added.Id, dto.TopUpId);
        Assert.Equal(TopUpSource.PromotionalCredit, added.Source);
        Assert.Equal("welcome", added.Reason);
        Assert.Equal("admin-1", added.ConfirmedBy);
        Assert.Equal(500_000L, added.Amount);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _repo.Verify(r => r.SaveChangesAsync(default), Times.Never);   // one save, through the wallet repo
    }

    [Fact]
    public async Task Grant_NonPositiveAmount_Throws_CreditsNothing()
    {
        var wallet = Wallet.Open(Tenant, "VND");
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(wallet);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().GrantAsync(Tenant, 0L, null));

        _walletRepo.Verify(r => r.AddTransaction(It.IsAny<WalletTransaction>()), Times.Never);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    // Over the config cap is refused and NOTHING is credited (check runs before any wallet load).
    [Fact]
    public async Task Grant_OverMaxCap_Throws_CreditsNothing()
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().GrantAsync(Tenant, 100_000_001L, null));

        Assert.Equal("GRANT_AMOUNT_EXCEEDS_MAX", ex.Code);
        _walletRepo.Verify(r => r.GetByTenantForUpdateAsync(It.IsAny<Guid>(), default), Times.Never);
        _repo.Verify(r => r.AddTopUp(It.IsAny<TopUp>()), Times.Never);
    }

    [Fact]
    public async Task Grant_ExactlyAtCap_Allowed()
    {
        var wallet = Wallet.Open(Tenant, "VND");
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(wallet);
        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only an id, so the amount
        // actually granted is captured here. Asserting only "it did not throw" would let a grant of the
        // wrong size pass at exactly the boundary this test exists to pin.
        TopUp? added = null;
        _repo.Setup(r => r.AddTopUp(It.IsAny<TopUp>())).Callback<TopUp>(t => added = t);

        var dto = await CreateService().GrantAsync(Tenant, 100_000_000L, null);

        Assert.NotNull(added);
        Assert.Equal(added!.Id, dto.TopUpId);
        Assert.Equal(100_000_000L, added.Amount);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Grant_NoWallet_ThrowsWalletNotFound_CreditsNothing()
    {
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync((Wallet?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateService().GrantAsync(Tenant, 500_000L, null));

        Assert.Equal("WALLET_NOT_FOUND", ex.Code);
        _walletRepo.Verify(r => r.AddTransaction(It.IsAny<WalletTransaction>()), Times.Never);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Grant_ConcurrencyConflict_ThrowsGrantConflict()
    {
        var wallet = Wallet.Open(Tenant, "VND");
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(wallet);
        _walletRepo.Setup(r => r.SaveChangesAsync(default))
                   .ThrowsAsync(new ConcurrencyConflictException("WALLET_VERSION_CONFLICT", "boom"));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CreateService().GrantAsync(Tenant, 500_000L, null));
        Assert.Equal("GRANT_CONFLICT", ex.Code);
    }

    // ── List ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListMine_ReturnsCallerTopUps()
    {
        AsTenant();
        var t = TopUp.Create(Tenant, 100L, "VND");
        _repo.Setup(r => r.ListTopUpsByTenantAsync(Tenant, default)).ReturnsAsync([t]);

        var list = await CreateService().ListMineAsync();

        Assert.Single(list);
        Assert.Equal(t.Id, list[0].Id);
    }

    // Ordering is the repository's job, so this pins only the filter forwarding.
    [Fact]
    public async Task List_ForwardsSourceFilter_ToRepository()
    {
        var promo = TopUp.GrantPromo(Tenant, 200_000L, "VND", "b", "admin-1");
        TopUpSource? seen = null;
        _repo.Setup(r => r.ListTopUpsAsync(It.IsAny<TopUpSource?>(), default))
             .Callback<TopUpSource?, CancellationToken>((s, _) => seen = s)
             .ReturnsAsync([promo]);

        var list = await CreateService().ListAsync(TopUpSource.PromotionalCredit);

        Assert.Equal(TopUpSource.PromotionalCredit, seen);   // filter reached the repo
        Assert.Single(list);
        Assert.Equal("b", list[0].Reason);
    }

    /// A second instance of the SAME purchase as another actor committed it — what a re-read from the
    /// database hands back after this caller lost the race. Separate object on purpose.
    private static TopUp Reload(TopUp original, TopUpSource source, string confirmedBy, string paymentTxnRef)
    {
        var copy = TopUp.Create(original.TenantId, original.Amount, original.Currency);
        typeof(TopUp).GetProperty(nameof(TopUp.Id))!.SetValue(copy, original.Id);
        typeof(TopUp).GetProperty(nameof(TopUp.PaymentRef))!.SetValue(copy, original.PaymentRef);
        copy.Confirm(source, confirmedBy, paymentTxnRef);
        return copy;
    }
}
