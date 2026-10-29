using Titan.Domain.Aggregates.Billing.WalletMutation;

namespace Titan.Domain.Repositories.Billing;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — shares the DbContext with IWalletRepository, so
/// its SaveChanges commits MarkApplied together with the balance change and ledger row in one transaction.
public interface IWalletMutationRepository
{
    /// Tracked. Null for a notify whose mutation never landed — nothing to apply.
    Task<WalletMutation?> GetForUpdateAsync(Guid mutationId, CancellationToken ct);

    /// Committed in the same SaveChanges as the business fact, so a TopUp is Confirmed and its credit mutation
    /// land together or not at all. UNIQUE(ref_type, ref_id) makes a second mutation from the same source fail
    /// at commit, whatever the first one's status.
    void Add(WalletMutation mutation);

    /// Oldest first, tracked. The age cutoff keeps the sweeper off the hot path: a mutation enqueued a moment
    /// ago belongs to the bus notify already in flight, and sweeping it would put two writers on one wallet.
    /// FOR UPDATE SKIP LOCKED holds its lock only for the statement, so it does not keep the sweep off a mutation
    /// the consumer is applying (titan-ops features/billing/wallet/design.md#wallet-debts).
    Task<IReadOnlyList<WalletMutation>> ListPendingOlderThanAsync(
        DateTimeOffset cutoff, int batchSize, CancellationToken ct);

    /// The source's one mutation, untracked. Backs the payment state an invoice shows its owner
    /// (Processing / Settled / Failed).
    Task<WalletMutation?> GetForRefAsync(string refType, Guid refId, CancellationToken ct);

    /// The source's one mutation, tracked, for reopening a failed invoice debit
    /// (titan-ops features/billing/invoice/design.md#retry-after-failed-debit).
    Task<WalletMutation?> GetForRefForUpdateAsync(string refType, Guid refId, CancellationToken ct);

    /// One query for the whole invoice list, not a per-row lookup in a loop.
    Task<IReadOnlyDictionary<Guid, WalletMutation>> GetForRefsAsync(
        string refType, IReadOnlyCollection<Guid> refIds, CancellationToken ct);

    /// Backs /healthz/wallet. Counted in the database, not by loading mutations, so the check does not slow
    /// down as the backlog grows. Poison means attempts exhausted, not merely Failed: a wallet that could not
    /// cover an invoice is a designed outcome, not a fault.
    Task<WalletMutationBacklog> GetHealthSnapshotAsync(
        DateTimeOffset now, int maxAttempts, CancellationToken ct);
}

/// The sweeper's own pulse lives in memory (WalletSweeperHeartbeat); the health check joins the two.
public sealed record WalletMutationBacklog(
    int PoisonCount,
    int PendingCount,
    TimeSpan? OldestPendingAge);
