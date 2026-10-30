
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.WalletMutation;

/// One row of the wallet outbox (titan-ops architecture/money-and-wallet.md#single-writer-outbox): a request
/// writes it next to the business fact, and the single-writer consumer applies it to the wallet later.
/// UNIQUE(ref_type, ref_id) gives one source document exactly one row, whatever its status; paying an invoice
/// again reopens its failed debit row (titan-ops features/billing/invoice/design.md#retry-after-failed-debit).
public class WalletMutation : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public WalletMutationType Type { get; private set; }

    /// Whole VND, always positive: Type carries the direction, because Wallet.Credit and Wallet.Debit both
    /// take a positive amount.
    public long Amount { get; private set; }

    /// "TopUp" or "Invoice". The source is identified by the pair (RefType, RefId), and the pair is the
    /// idempotency key of the mutation.
    public string RefType { get; private set; } = default!;

    public Guid RefId { get; private set; }

    public WalletMutationStatus Status { get; private set; }

    /// When the source first asked for this mutation; a reopen keeps it.
    public DateTimeOffset CreatedAt { get; private set; }

    /// When the mutation last became Pending: CreatedAt, or the moment it was reopened. The sweeper's age cutoff
    /// and the pending age of /healthz/wallet read this, so a reopened row is fresh to both.
    public DateTimeOffset QueuedAt { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    private WalletMutation() { }

    public static WalletMutation ForCredit(Guid walletId, long amount, string refType, Guid refId) =>
        Create(walletId, WalletMutationType.Credit, amount, refType, refId);

    /// The non-negative balance rule stays in Wallet.Debit; this only records the request.
    public static WalletMutation ForDebit(Guid walletId, long amount, string refType, Guid refId) =>
        Create(walletId, WalletMutationType.Debit, amount, refType, refId);

    private static WalletMutation Create(
        Guid walletId, WalletMutationType type, long amount, string refType, Guid refId)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A wallet mutation must carry a positive amount; Type is the sign.");
        if (string.IsNullOrWhiteSpace(refType))
            throw new ArgumentException("A mutation must name the kind of document it came from.", nameof(refType));

        var now = DateTimeOffset.UtcNow;
        return new WalletMutation
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Type = type,
            Amount = amount,
            RefType = refType,
            RefId = refId,
            Status = WalletMutationStatus.Pending,
            CreatedAt = now,
            QueuedAt = now,
            AppliedAt = null,
            Attempts = 0,
            LastError = null
        };
    }

    /// Terminal absorbs every later Mark* on this instance (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// It does not guard the money: the consumer moves it before MarkApplied (titan-ops features/billing/wallet/design.md#wallet-debts).
    public bool IsTerminal => Status is WalletMutationStatus.Applied or WalletMutationStatus.Failed;

    /// An observation (titan-ops architecture/intent-and-observation.md#observation-converges): never throws.
    public void MarkApplied()
    {
        if (IsTerminal) return;
        Status = WalletMutationStatus.Applied;
        AppliedAt = DateTimeOffset.UtcNow;
    }

    /// The wallet could not cover it, or the sweeper gave up on it. AppliedAt stays null: it answers
    /// "when did money move", and for a failed mutation it never did.
    public void MarkFailed(string reason)
    {
        if (IsTerminal) return;
        Status = WalletMutationStatus.Failed;
        LastError = reason;
    }

    /// An intent (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): the payer pressed pay again
    /// after the debit failed (titan-ops features/billing/invoice/design.md#retry-after-failed-debit). Throws on
    /// anything but a failed debit; a credit comes from a top-up confirmed once and is never applied again.
    /// The attempt count starts over so a row the sweeper gave up on gets its full retries; LastError stays as
    /// the only trace of why the previous try failed.
    public void ReopenFailedDebit()
    {
        if (Type != WalletMutationType.Debit)
            throw new InvalidOperationException(
                $"Mutation {Id} is a {Type}; only a failed debit can be reopened.");
        if (Status != WalletMutationStatus.Failed)
            throw new InvalidOperationException(
                $"Mutation {Id} is {Status}; only a failed debit can be reopened.");

        Status = WalletMutationStatus.Pending;
        Attempts = 0;
        QueuedAt = DateTimeOffset.UtcNow;
    }

    /// Stays Pending: one failed attempt is a retry, not a verdict. The give-up threshold is the
    /// sweeper's configuration, not an invariant of the mutation.
    public void RecordAttempt(string error)
    {
        if (IsTerminal) return;
        Attempts++;
        LastError = error;
    }
}
