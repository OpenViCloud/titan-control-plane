namespace Titan.Application.Services.Billing.WalletMutations;

/// <summary>
/// The async wallet path for the health check (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
/// Two independent kinds of evidence, the backlog and the sweeper's passes, because either alone can read "fine" while
/// the path is broken: a quiet system and one whose sweeper stopped both show zero pending, and a sweeper whose every
/// apply fails still completes passes.
/// </summary>
/// <param name="PoisonCount">
/// Mutations that exhausted their attempts. Not "status Failed": a debit the wallet could not cover is a
/// designed outcome that needs no operator, and merging the two would bury the real fault.
/// </param>
/// <param name="PendingCount">Mutations still waiting to be applied, at any age.</param>
/// <param name="OldestPendingAge">
/// Null when nothing is waiting. This answers "is money stuck?": many young mutations are a busy
/// system, one old mutation is a stuck one.
/// </param>
/// <param name="LastSweepAt">Last successful pass; null until the first — see <paramref name="SweeperRegisteredAt"/>.</param>
/// <param name="SweeperRegisteredAt">
/// When the sweeper's migration registered it: the clock while it has never swept, so the release that adds it raises no
/// false alarm and a CronJob that never runs still shows. Null when no row registers it, which is a fault.
/// </param>
public sealed record WalletMutationHealth(
    int PoisonCount,
    int PendingCount,
    TimeSpan? OldestPendingAge,
    DateTimeOffset? LastSweepAt,
    DateTimeOffset? SweeperRegisteredAt);
