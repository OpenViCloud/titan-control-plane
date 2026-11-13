namespace Titan.Application.Services.Billing.WalletMutations;

/// <summary>
/// The wallet sweeper's last attempt and last success, written by its scheduled job and read by /healthz/wallet: the
/// sweeper is the only thing that recovers a lost notify, and a sweeper that stopped leaves no error anywhere. Kept where
/// every control plane pod reads the same answer, whichever pod the CronJob reached
/// (titan-ops features/billing/wallet/design.md#healthz-wallet).
/// </summary>
public interface IWalletSweeps
{
    Task RecordAsync(bool success, DateTimeOffset at, CancellationToken ct);

    /// Null when no migration registered the sweeper.
    Task<WalletSweepState?> GetAsync(CancellationToken ct);
}
