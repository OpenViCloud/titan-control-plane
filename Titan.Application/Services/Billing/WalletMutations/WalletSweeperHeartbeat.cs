namespace Titan.Application.Services.Billing.WalletMutations;

/// <summary>
/// The sweeper's pulse, like <see cref="MeterHeartbeat"/>: it lets the health check see a sweeper that stopped,
/// which a log line alone does not. The sweeper is the only thing that recovers a lost notify
/// (titan-ops architecture/money-and-wallet.md#single-writer-outbox); while it is dead, a mutation whose notify was lost is
/// never applied.
/// No lock: a DateTimeOffset? read from the health-check thread can tear, and a health signal tolerates that.
/// </summary>
public sealed class WalletSweeperHeartbeat
{
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastSweepAt;

    /// <summary>Lets the health check tell "has not swept yet" from "has stopped sweeping".</summary>
    public void Started(DateTimeOffset at) => _startedAt = at;

    /// <summary>Includes a pass that found nothing: finding nothing is the sweeper working.</summary>
    public void Swept(DateTimeOffset at) => _lastSweepAt = at;

    public DateTimeOffset? StartedAt => _startedAt;
    public DateTimeOffset? LastSweepAt => _lastSweepAt;
}
