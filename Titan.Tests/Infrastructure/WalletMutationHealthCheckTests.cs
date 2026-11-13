using Titan.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the verdict on the async money path. Tested
/// through the pure Evaluate(now, state, thresholds) so the clock is an injected oracle rather than
/// DateTimeOffset.UtcNow, and the backlog figures are hand-built: the oracle sits outside the thing
/// under test.
///
/// The pending threshold is not the SLO. The SLO (~5-10s, titan-ops architecture/money-and-wallet.md#single-writer-outbox)
/// describes the FAST path — the bus notify. When a notify is lost the sweeper's CronJob is what recovers the mutation,
/// and its worst case is StaleAfterSeconds (10) + IntervalSeconds (120) = 130s. Alerting at the SLO would fire every
/// time the safety net does its job. The threshold is twice that worst case, 260s, leaving room for one missed run.
public class WalletMutationHealthCheckTests
{
    private const int PendingThresholdSeconds = 260;
    private const int SweepIntervalSeconds = 120;
    private const int SweepStaleFactor = 3; // a sweeper is stale after 6 minutes without a successful pass
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static HealthCheckResult Evaluate(WalletMutationHealth state) =>
        WalletMutationHealthCheck.Evaluate(
            Now, state, PendingThresholdSeconds, SweepIntervalSeconds, SweepStaleFactor);

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The distinction that decides whether this check is worth having.
    //
    // A mutation Failed because the wallet could not cover the invoice at apply time is a NORMAL,
    // designed outcome (titan-ops architecture/money-and-wallet.md#single-writer-outbox, risk-accepted): the customer spent their balance elsewhere
    // between pressing pay and the consumer running. Nobody should be woken for it. A mutation Failed
    // because it exhausted its attempts is POISON: a payment is stuck with nobody looking at it.
    //
    // Counting `status = Failed` would merge the two and make the signal useless in the direction
    // that matters — every ordinary shortfall would bury the one real fault.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Poison_mutation_is_Unhealthy()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 1,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperRegisteredAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("poison", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Business_failure_without_poison_is_Healthy()
    {
        // The wallet ran short mid-apply: mutation Failed, invoice unpaid, customer can retry. The
        // design working, not a fault. PoisonCount is what separates this from the case above.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddSeconds(-5),
            SweeperRegisteredAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Backlog age, measured against the RECOVERY worst case rather than the SLO.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pending_mutation_older_than_the_threshold_is_Unhealthy_and_says_how_old()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: TimeSpan.FromSeconds(300),
            PendingCount: 3,
            LastSweepAt: Now.AddSeconds(-30),
            SweeperRegisteredAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("300", result.Description);
    }

    [Fact]
    public void Pending_mutation_the_sweeper_has_not_reached_yet_is_Healthy()
    {
        // 60s old: younger than StaleAfterSeconds(10) + IntervalSeconds(120). The sweeper's CronJob has not had its turn,
        // and complaining here would mean alerting on the safety net performing exactly as designed.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: TimeSpan.FromSeconds(60),
            PendingCount: 1,
            LastSweepAt: Now.AddSeconds(-30),
            SweeperRegisteredAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void A_mutation_at_the_sweeper_worst_case_is_still_Healthy()
    {
        // 130s = StaleAfterSeconds + IntervalSeconds exactly. The boundary is deliberate: this is the slowest a
        // HEALTHY recovery can be, so it must not read as a fault.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: TimeSpan.FromSeconds(130),
            PendingCount: 1,
            LastSweepAt: Now.AddSeconds(-30),
            SweeperRegisteredAt: Now.AddHours(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // "Never ran" must not read as "healthy": a sweeper whose CronJob runs perfectly and one whose CronJob does not
    // exist both produce zero activity. Until the first pass, the clock starts at the row the migration wrote.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sweeper_that_has_never_run_since_its_row_was_registered_is_Unhealthy()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: null,
            SweeperRegisteredAt: Now.AddMinutes(-10))); // past three runs of its CronJob

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("never", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sweeper_registered_within_its_stale_window_is_Healthy()
    {
        // Registered 5 minutes ago with a 2-minute CronJob and a factor of 3: it has not missed three runs yet. Flagging
        // this would make the release that adds the row raise a false alarm.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: null,
            SweeperRegisteredAt: Now.AddMinutes(-5)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Sweeper_without_its_row_is_Unhealthy()
    {
        // No row: no migration registered the sweeper, so nothing says when it should have run. With a non-persistent
        // bus, an absent safety net loses money silently (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: null,
            SweeperRegisteredAt: null));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void Sweeper_that_stopped_sweeping_is_Unhealthy()
    {
        // It ran, then stopped: 10 minutes without a successful pass on a 2-minute CronJob (suspended, or every pass
        // failing). The safety net is gone and nothing else in the system would say so.
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddMinutes(-10),
            SweeperRegisteredAt: Now.AddDays(-1)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("sweep", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quiet_and_healthy_system_reports_Healthy()
    {
        var result = Evaluate(new WalletMutationHealth(
            PoisonCount: 0,
            OldestPendingAge: null,
            PendingCount: 0,
            LastSweepAt: Now.AddSeconds(-100),
            SweeperRegisteredAt: Now.AddDays(-1)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}

/// The thresholds production uses, pinned separately.
///
/// Why this class exists: the suite above passes the threshold in as an argument, which measures the comparison logic
/// and is SILENT about what production actually uses — setting the real threshold to the SLO value leaves it green.
///
/// These assert the RELATIONSHIP, not digits: the threshold follows the sweeper's timers, so moving the CronJob to a
/// longer interval moves the threshold with it instead of producing an alert that fires whenever the safety net works.
public class WalletMutationHealthThresholdTests
{
    [Fact]
    public void Pending_threshold_clears_the_sweepers_worst_case_recovery_twice()
    {
        var defaults = new WalletSweeperOptions();
        var worstCaseRecovery = defaults.StaleAfterSeconds + defaults.IntervalSeconds;

        Assert.True(
            WalletMutationHealthCheck.PendingThresholdSeconds(defaults) >= 2 * worstCaseRecovery,
            $"threshold {WalletMutationHealthCheck.PendingThresholdSeconds(defaults)}s must be at least twice the " +
            $"sweeper's worst-case recovery of {worstCaseRecovery}s, or the check fires every time the safety net works");
    }

    [Fact]
    public void Pending_threshold_follows_the_sweep_interval()
    {
        var shortInterval = new WalletSweeperOptions { IntervalSeconds = 30 };
        var longInterval = new WalletSweeperOptions { IntervalSeconds = 300 };

        Assert.True(
            WalletMutationHealthCheck.PendingThresholdSeconds(longInterval) >
            WalletMutationHealthCheck.PendingThresholdSeconds(shortInterval));
    }

    [Fact]
    public void Pending_threshold_is_not_set_to_the_fast_path_SLO()
    {
        // The ~5-10s SLO of titan-ops architecture/money-and-wallet.md#single-writer-outbox describes the bus notify path.
        // Using it here is the specific mistake this test exists to catch: it would alert on every sweeper-recovered mutation.
        Assert.True(WalletMutationHealthCheck.PendingThresholdSeconds(new WalletSweeperOptions()) > 10);
    }

    [Fact]
    public void Sweeper_stale_factor_tolerates_more_than_one_missed_pass()
    {
        // One slow or skipped run is not a dead sweeper. Alerting on a single missed run is how a signal gets trained out
        // of an operator.
        Assert.True(WalletMutationHealthCheck.SweepStaleFactor >= 2);
    }

    [Fact]
    public void The_sweep_interval_is_the_cronjobs_two_minutes()
    {
        // titan-local-cluster base/control-plane/scheduled-jobs.yaml runs wallet-sweep every 2 minutes; its render test
        // compares the schedule with WalletSweeper__IntervalSeconds. The default is that same interval.
        Assert.Equal(120, new WalletSweeperOptions().IntervalSeconds);
    }
}
