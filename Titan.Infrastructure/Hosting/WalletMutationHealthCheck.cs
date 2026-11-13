using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Every failure of the async wallet path is quiet: a lost notify, a stopped sweeper and a poison mutation
/// all just leave rows Pending (titan-ops architecture/money-and-wallet.md#single-writer-outbox). Not an alert:
/// nothing polls this (titan-ops infra/runbooks/runbook-wallet-async.md). Both inputs are rows, so every pod answers the
/// same whichever pod the sweeper's CronJob reached.
/// </summary>
public sealed class WalletMutationHealthCheck(
    IWalletMutationRepository mutations,
    IWalletSweeps sweeps,
    IOptions<WalletSweeperOptions> options) : IHealthCheck
{
    /// Three missed passes rather than one: a single slow or skipped run is not a fault.
    public const int SweepStaleFactor = 3;

    /// Not the fast-path SLO: it must clear the sweeper's recovery of a lost notify (StaleAfterSeconds + IntervalSeconds)
    /// twice, or it fires every time the safety net works.
    public static int PendingThresholdSeconds(WalletSweeperOptions opts) =>
        2 * (opts.StaleAfterSeconds + opts.IntervalSeconds);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var opts = options.Value;
        var now = DateTimeOffset.UtcNow;

        var backlog = await mutations.GetHealthSnapshotAsync(now, opts.MaxAttempts, ct);
        var sweep = await sweeps.GetAsync(ct);

        var state = new WalletMutationHealth(
            PoisonCount: backlog.PoisonCount,
            PendingCount: backlog.PendingCount,
            OldestPendingAge: backlog.OldestPendingAge,
            LastSweepAt: sweep?.LastSuccessAt,
            SweeperRegisteredAt: sweep?.RegisteredAt);

        return Evaluate(now, state, PendingThresholdSeconds(opts), opts.IntervalSeconds, SweepStaleFactor);
    }

    /// <summary>
    /// Pure, so WalletMutationHealthCheckTests can inject the clock and state. Ordered worst-first, and each
    /// branch names which of the unrelated faults broke.
    /// </summary>
    public static HealthCheckResult Evaluate(
        DateTimeOffset now,
        WalletMutationHealth state,
        int pendingThresholdSeconds,
        int sweepIntervalSeconds,
        int sweepStaleFactor)
    {
        // Outranks an empty backlog: with no sweeper registered, a quiet queue is the absence of a witness, not health.
        if (state.SweeperRegisteredAt is null)
            return HealthCheckResult.Unhealthy(
                "no wallet sweeper is registered — a lost notify will never be recovered (titan-ops architecture/money-and-wallet.md#single-writer-outbox)");

        if (state.PoisonCount > 0)
            return HealthCheckResult.Unhealthy(
                $"{state.PoisonCount} poison wallet mutation(s) given up on — a payment is stuck");

        var staleWindow = TimeSpan.FromSeconds((double)sweepIntervalSeconds * sweepStaleFactor);

        // Never-run and running-fine both show an empty backlog, so a sweeper that never swept is measured from its row.
        if (state.LastSweepAt is null)
        {
            if (now - state.SweeperRegisteredAt.Value > staleWindow)
                return HealthCheckResult.Unhealthy(
                    $"wallet sweeper has never completed a pass in the {(int)(now - state.SweeperRegisteredAt.Value).TotalSeconds}s " +
                    $"since it was registered (expected every {sweepIntervalSeconds}s) — its CronJob is not running");
        }
        else if (now - state.LastSweepAt.Value > staleWindow)
        {
            return HealthCheckResult.Unhealthy(
                $"wallet sweeper last swept {(int)(now - state.LastSweepAt.Value).TotalSeconds}s ago " +
                $"(expected every {sweepIntervalSeconds}s) — the safety net has stopped");
        }

        // Last: the machinery is alive here, so an old mutation means the work itself is not getting through.
        if (state.OldestPendingAge is { } age && age.TotalSeconds > pendingThresholdSeconds)
            return HealthCheckResult.Unhealthy(
                $"oldest pending wallet mutation is {(int)age.TotalSeconds}s old " +
                $"(threshold {pendingThresholdSeconds}s), {state.PendingCount} pending");

        return HealthCheckResult.Healthy(
            state.PendingCount == 0
                ? "no wallet mutations pending; sweeper alive"
                : $"{state.PendingCount} wallet mutation(s) pending within the expected window; sweeper alive");
    }
}
