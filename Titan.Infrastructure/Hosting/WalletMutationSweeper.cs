using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// The polling relay that makes a non-persistent NATS bus safe on a money path: the mutation is a durable
/// row, and this re-drives the rows the bus did not get through
/// (titan-ops architecture/money-and-wallet.md#single-writer-outbox). Without it a lost notify loses money silently.
/// It calls the same IWalletMutationConsumer.ApplyAsync as the bus, so there is one apply path.
/// </summary>
public class WalletMutationSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<WalletSweeperOptions> options,
    WalletSweeperHeartbeat heartbeat,
    ILogger<WalletMutationSweeper> logger) : BackgroundService
{
    private readonly WalletSweeperOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recorded before the disabled check, so /healthz/wallet can tell "switched off" from "not wired up".
        heartbeat.Started(DateTimeOffset.UtcNow);

        if (!_opts.Enabled)
        {
            // Loud on purpose: running without the safety net should show in the logs.
            logger.LogWarning(
                "Wallet mutation sweeper is DISABLED. Mutations the notify bus fails to deliver will " +
                "stay Pending indefinitely (docs/architecture/money-and-wallet.md#single-writer-outbox).");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opts.IntervalSeconds));

        // Wait-first: nothing is stale at startup.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var swept = await SweepOnceAsync(
                    scope.ServiceProvider.GetRequiredService<IWalletMutationRepository>(),
                    stoppingToken);

                // A pass that found nothing still counts; only a pass that threw leaves the pulse untouched,
                // so a sweeper failing every time is distinguishable from a healthy quiet one.
                heartbeat.Swept(DateTimeOffset.UtcNow);

                if (swept > 0)
                    logger.LogInformation("Wallet sweeper re-drove {Count} stale mutation(s)", swept);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad pass must not kill the net: the mutations stay Pending and the next tick retries.
                logger.LogError(ex, "Wallet sweeper pass failed");
            }
        }
    }

    /// <summary>
    /// Returns how many stale mutations were claimed. Public so tests run a pass without a PeriodicTimer
    /// (WalletMutationSweeperDbTests).
    ///
    /// Each apply gets its own scope. The list's context tracks every listed mutation as Pending, so an apply
    /// through it misses one the subscriber applied meanwhile; and an apply that throws leaves the wallet it
    /// changed in that context, for the next mutation's save to commit (scenario: WalletMutationSweeperTests
    /// .Sweep_MutationAppliedElsewhereAfterTheList_MovesTheMoneyOnce).
    /// </summary>
    public async Task<int> SweepOnceAsync(IWalletMutationRepository mutationRepo, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_opts.StaleAfterSeconds);
        var stale = await mutationRepo.ListPendingOlderThanAsync(cutoff, _opts.BatchSize, ct);

        foreach (var mutation in stale)
        {
            try
            {
                using var applyScope = scopeFactory.CreateScope();
                await applyScope.ServiceProvider.GetRequiredService<IWalletMutationConsumer>()
                    .ApplyAsync(mutation.Id, ct);
            }
            catch (Exception ex)
            {
                try
                {
                    await GiveUpOrRetryAsync(mutation.Id, ex, ct);
                }
                catch (ConcurrencyConflictException)
                {
                    // The row's status moved between the read and the save (applied by the subscriber, or
                    // given up and reopened by a payer): the status token refused to record an attempt on it.
                    // Nothing to record; the rest of the batch still runs.
                    logger.LogInformation(
                        "Wallet mutation {MutationId} changed while the sweeper recorded a failed attempt; skipped",
                        mutation.Id);
                }
            }
        }

        return stale.Count;
    }

    /// <summary>
    /// Loaded in a fresh scope: the failed apply left the previous ChangeTracker holding whatever it touched
    /// before it threw, and saving the verdict through it would commit those half-made changes too.
    /// </summary>
    private async Task GiveUpOrRetryAsync(Guid mutationId, Exception failure, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var mutationRepo = scope.ServiceProvider.GetRequiredService<IWalletMutationRepository>();
        var walletRepo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();

        var mutation = await mutationRepo.GetForUpdateAsync(mutationId, ct);
        if (mutation is null || mutation.IsTerminal) return;

        mutation.RecordAttempt(failure.Message);

        if (mutation.Attempts >= _opts.MaxAttempts)
        {
            mutation.MarkFailed(
                $"Given up after {mutation.Attempts} attempts. Last error: {failure.Message}");
            // A poison mutation means a customer's payment or top-up is stuck with nobody looking at it.
            logger.LogError(failure,
                "POISON wallet mutation {MutationId} (wallet {WalletId}, {RefType} {RefId}) " +
                "marked Failed after {Attempts} attempts",
                mutation.Id, mutation.WalletId, mutation.RefType, mutation.RefId, mutation.Attempts);
        }
        else
        {
            logger.LogWarning(failure,
                "Wallet mutation {MutationId} failed attempt {Attempts}; will retry",
                mutation.Id, mutation.Attempts);
        }

        await walletRepo.SaveChangesAsync(ct);
    }
}
