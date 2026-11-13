namespace Titan.Infrastructure.Persistence;

/// The wallet sweeper's passes (Titan.Application.Services.Billing.WalletMutations.IWalletSweeps): one row, keyed by the
/// job's name. Not a domain aggregate.
public class WalletSweep
{
    public string Job { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}
