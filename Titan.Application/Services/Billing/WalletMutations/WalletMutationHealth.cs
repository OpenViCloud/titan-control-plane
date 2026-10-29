namespace Titan.Application.Services.Billing.WalletMutations;

/// <summary>
/// The async wallet path for the health check (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
/// Two independent kinds of evidence, the backlog (database) and the sweeper's pulse (memory), because
/// either alone can read "fine" while the path is broken: a quiet system and one whose sweeper died both
/// show zero pending, and a sweeper whose every apply fails still pulses.
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
/// <param name="LastSweepAt">
/// Null if no pass has completed since this process started, which is not healthy by default — see
/// <paramref name="SweeperStartedAt"/>.
/// </param>
/// <param name="SweeperStartedAt">
/// Tells "has not swept yet" from "has stopped sweeping", so a deploy raises no false alarm.
/// </param>
/// <param name="SweeperEnabled">
/// Off is a fault however quiet the backlog: with a non-persistent bus, a disabled sweeper loses money
/// without an error anywhere (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
/// </param>
public sealed record WalletMutationHealth(
    int PoisonCount,
    int PendingCount,
    TimeSpan? OldestPendingAge,
    DateTimeOffset? LastSweepAt,
    DateTimeOffset? SweeperStartedAt,
    bool SweeperEnabled);
