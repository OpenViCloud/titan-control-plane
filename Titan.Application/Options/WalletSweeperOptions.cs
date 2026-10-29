namespace Titan.Application.Options;

/// <summary>
/// The sweeper re-drives wallet mutations the fast path did not finish, which is what makes a
/// non-persistent bus safe (titan-ops architecture/money-and-wallet.md#single-writer-outbox): a lost notify leaves the
/// row Pending, and without the sweeper nothing would look at it again.
/// </summary>
public class WalletSweeperOptions
{
    public const string SectionName = "WalletSweeper";

    /// <summary>
    /// On by default, unlike the NATS bus: the bus being off costs latency, the sweeper being off removes
    /// the only safety net.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Keeps the sweeper off the hot path: a mutation enqueued a moment ago belongs to a notify already in
    /// flight, and claiming it would put a second writer on the wallet.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 10;

    /// <summary>Mutations claimed per sweep, so one pass stays short; each applies in its own transaction.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Failed attempts before a mutation is declared poison and marked Failed. Retrying forever would hide
    /// a broken mutation behind an endlessly "pending" payment; giving up records a reason a human can act
    /// on, and /healthz/wallet reports it.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;
}
