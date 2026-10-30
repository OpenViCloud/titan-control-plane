using Titan.Domain.Aggregates.WalletMutation;

namespace Titan.Tests.Domain;

// The outbox mutation (titan-ops architecture/money-and-wallet.md#single-writer-outbox). What is worth testing without a
// database: the factory sets a Pending mutation with a POSITIVE amount whose SIGN is Type's job, not the caller's.
// The idempotency GUARD itself is a DB UNIQUE(ref_type, ref_id), not tested here (InMemory does not enforce
// indexes — see WalletMutationSchemaTests for the metadata pin).
public class WalletMutationTests
{
    private static readonly Guid Wallet = Guid.NewGuid();

    [Fact]
    public void ForCredit_IsPendingCreditFromItsSource()
    {
        var topUpId = Guid.NewGuid();

        var cmd = WalletMutation.ForCredit(Wallet, 500_000L, "TopUp", topUpId);

        Assert.Equal(Wallet, cmd.WalletId);
        Assert.Equal(WalletMutationType.Credit, cmd.Type);
        Assert.Equal(500_000L, cmd.Amount);              // positive — Type carries the sign
        Assert.Equal("TopUp", cmd.RefType);
        Assert.Equal(topUpId, cmd.RefId);
        Assert.Equal(WalletMutationStatus.Pending, cmd.Status);
        Assert.Equal(0, cmd.Attempts);
        Assert.Null(cmd.AppliedAt);
        Assert.Null(cmd.LastError);
        Assert.NotEqual(Guid.Empty, cmd.Id);
    }

    [Fact]
    public void ForDebit_IsPendingDebitFromItsSource()
    {
        var invoiceId = Guid.NewGuid();

        var cmd = WalletMutation.ForDebit(Wallet, 30_000L, "Invoice", invoiceId);

        Assert.Equal(WalletMutationType.Debit, cmd.Type);
        Assert.Equal(30_000L, cmd.Amount);               // positive — Debit takes a positive amount too
        Assert.Equal("Invoice", cmd.RefType);
        Assert.Equal(invoiceId, cmd.RefId);
        Assert.Equal(WalletMutationStatus.Pending, cmd.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Factory_RejectsNonPositiveAmount(long bad)
    {
        // The amount is always positive; a negative "credit" would be a debit wearing the wrong label,
        // exactly what WalletTransaction refuses one layer down.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WalletMutation.ForCredit(Wallet, bad, "TopUp", Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WalletMutation.ForDebit(Wallet, bad, "Invoice", Guid.NewGuid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Factory_RejectsBlankRefType(string bad)
    {
        // (RefType, RefId) is the idempotency key; a blank discriminator would collapse distinct
        // sources onto one key.
        Assert.Throws<ArgumentException>(() =>
            WalletMutation.ForCredit(Wallet, 1_000L, bad, Guid.NewGuid()));
    }

    // ── the consumer's transition (titan-ops architecture/intent-and-observation.md#observation-converges) ──────────────────────────
    // MarkApplied is an observation, so it never throws a transition error; IsTerminal is the defense-in-depth
    // beneath the consumer's own status check. The absorb's behavioural teeth are proven at the integration layer
    // (WalletMutationConsumerDbTests: a redelivered mutation credits the wallet once) — NOT by comparing two
    // UtcNow stamps here, which the clock's coarseness could make lie.

    [Fact]
    public void Pending_IsNotTerminal_Applied_Is()
    {
        var cmd = WalletMutation.ForCredit(Wallet, 100_000L, "TopUp", Guid.NewGuid());
        Assert.False(cmd.IsTerminal);   // a Pending mutation is still work to do

        cmd.MarkApplied();

        Assert.True(cmd.IsTerminal);    // Applied absorbs a re-apply — the redelivery guard
    }

    [Fact]
    public void MarkApplied_FromPending_BecomesApplied_AndStampsAppliedAt()
    {
        var cmd = WalletMutation.ForCredit(Wallet, 100_000L, "TopUp", Guid.NewGuid());

        cmd.MarkApplied();

        Assert.Equal(WalletMutationStatus.Applied, cmd.Status);
        Assert.NotNull(cmd.AppliedAt);   // the moment the single writer applied it
    }

    // ---- MarkFailed + RecordAttempt (titan-ops architecture/money-and-wallet.md#single-writer-outbox) ----
    // Failed is reached by the consumer's insufficient-funds re-check and by the sweeper giving up on a poison
    // mutation. Both are OBSERVATIONS (titan-ops architecture/intent-and-observation.md#observation-converges) — they never throw a transition error.

    [Fact]
    public void MarkFailed_RecordsTheReason_AndIsTerminal()
    {
        var cmd = WalletMutation.ForDebit(Wallet, 100_000L, "Invoice", Guid.NewGuid());

        cmd.MarkFailed("Wallet holds 0; the debit needs 100000.");

        Assert.Equal(WalletMutationStatus.Failed, cmd.Status);
        Assert.Equal("Wallet holds 0; the debit needs 100000.", cmd.LastError);
        Assert.True(cmd.IsTerminal);
        // A failed mutation never applied anything, so it carries no AppliedAt — the column answers
        // "when did money move", and for this mutation money never moved.
        Assert.Null(cmd.AppliedAt);
    }

    [Fact]
    public void MarkFailed_IsTerminal_AbsorbsReapply()
    {
        // The absorb is what makes a redelivered notify or a second sweep safe on a DEAD mutation:
        // nothing about it may change, least of all back to Pending, which would re-open a debit that
        // was already reported failed to the customer.
        var cmd = WalletMutation.ForDebit(Wallet, 100_000L, "Invoice", Guid.NewGuid());
        cmd.MarkFailed("first reason");

        cmd.MarkApplied();
        cmd.MarkFailed("second reason");

        Assert.Equal(WalletMutationStatus.Failed, cmd.Status);
        Assert.Equal("first reason", cmd.LastError);
        Assert.Null(cmd.AppliedAt);
    }

    [Fact]
    public void MarkApplied_IsTerminal_AbsorbsAFailure()
    {
        // The mirror of the test above, and the one that matters for money: a mutation that already
        // moved the balance must not be talked into Failed by a late sweep, or the customer is told a
        // payment failed that in fact went through.
        var cmd = WalletMutation.ForCredit(Wallet, 500_000L, "TopUp", Guid.NewGuid());
        cmd.MarkApplied();

        cmd.MarkFailed("a late sweeper decided this was poison");

        Assert.Equal(WalletMutationStatus.Applied, cmd.Status);
        Assert.Null(cmd.LastError);
        Assert.NotNull(cmd.AppliedAt);
    }

    [Fact]
    public void RecordAttempt_CountsTheTry_AndKeepsItPending()
    {
        // The sweeper bumps this on every failed pass. It stays Pending: one bad attempt is not a
        // verdict, it is a retry. Only crossing MaxAttempts (a policy the sweeper owns, not the
        // aggregate) turns into MarkFailed.
        var cmd = WalletMutation.ForCredit(Wallet, 500_000L, "TopUp", Guid.NewGuid());

        cmd.RecordAttempt("connection reset");
        cmd.RecordAttempt("connection reset again");

        Assert.Equal(2, cmd.Attempts);
        Assert.Equal("connection reset again", cmd.LastError);
        Assert.Equal(WalletMutationStatus.Pending, cmd.Status);
        Assert.False(cmd.IsTerminal);
    }

    [Fact]
    public void RecordAttempt_OnATerminalMutation_ChangesNothing()
    {
        var cmd = WalletMutation.ForCredit(Wallet, 500_000L, "TopUp", Guid.NewGuid());
        cmd.MarkApplied();

        cmd.RecordAttempt("a sweeper that should not have picked this up");

        Assert.Equal(0, cmd.Attempts);
        Assert.Null(cmd.LastError);
        Assert.Equal(WalletMutationStatus.Applied, cmd.Status);
    }

    // titan-ops features/billing/invoice/design.md#retry-after-failed-debit — paying again reopens the same row.
    [Fact]
    public void ReopenFailedDebit_MakesItPendingAgain_WithAFreshQueueTime_AndNoAttemptsSpent()
    {
        var debit = WalletMutation.ForDebit(Wallet, 300_000L, "Invoice", Guid.NewGuid());
        for (var i = 0; i < 5; i++)
        {
            debit.RecordAttempt("database unreachable");
        }
        debit.MarkFailed("Given up after 5 attempts.");
        var createdAt = debit.CreatedAt;
        var before = DateTimeOffset.UtcNow;

        debit.ReopenFailedDebit();

        Assert.Equal(WalletMutationStatus.Pending, debit.Status);
        Assert.False(debit.IsTerminal);
        Assert.Equal(0, debit.Attempts);
        Assert.Equal(createdAt, debit.CreatedAt);
        Assert.True(debit.QueuedAt >= before, $"QueuedAt {debit.QueuedAt:O} is older than the reopen {before:O}");
        Assert.Null(debit.AppliedAt);
        // The only trace of why the previous try failed.
        Assert.Equal("Given up after 5 attempts.", debit.LastError);
    }

    [Fact]
    public void ForDebit_QueuesAtCreation()
    {
        var debit = WalletMutation.ForDebit(Wallet, 300_000L, "Invoice", Guid.NewGuid());

        Assert.Equal(debit.CreatedAt, debit.QueuedAt);
    }

    [Fact]
    public void ReopenFailedDebit_OnAPendingDebit_Throws()
    {
        var debit = WalletMutation.ForDebit(Wallet, 300_000L, "Invoice", Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => debit.ReopenFailedDebit());
        Assert.Equal(WalletMutationStatus.Pending, debit.Status);
    }

    [Fact]
    public void ReopenFailedDebit_OnAnAppliedDebit_Throws()
    {
        var debit = WalletMutation.ForDebit(Wallet, 300_000L, "Invoice", Guid.NewGuid());
        debit.MarkApplied();

        Assert.Throws<InvalidOperationException>(() => debit.ReopenFailedDebit());
        Assert.Equal(WalletMutationStatus.Applied, debit.Status);
    }

    // A top-up is credited once: a failed credit is never put back in the queue.
    [Fact]
    public void ReopenFailedDebit_OnAFailedCredit_Throws()
    {
        var credit = WalletMutation.ForCredit(Wallet, 500_000L, "TopUp", Guid.NewGuid());
        credit.MarkFailed("Given up.");

        Assert.Throws<InvalidOperationException>(() => credit.ReopenFailedDebit());
        Assert.Equal(WalletMutationStatus.Failed, credit.Status);
    }
}
