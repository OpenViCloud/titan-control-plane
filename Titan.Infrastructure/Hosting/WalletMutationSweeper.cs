using Microsoft.Extensions.DependencyInjection;
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
/// It calls the same IWalletMutationConsumer.ApplyAsync as the bus, so there is one apply path. A pass runs when the
/// sweeper's CronJob calls POST /internal/jobs/wallet-sweep (titan-ops architecture/background-jobs.md#background-jobs).
/// No job lock: two passes at once may list the same rows (the list's locks end with its statement), and the consumer's
/// row lock, read under which a mutation no longer Pending is left alone, moves the money once.
/// </summary>
public class WalletMutationSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<WalletSweeperOptions> options,
    ILogger<WalletMutationSweeper> logger) : IScheduledJob
{
    public const string JobName = "wallet-sweep";

    private readonly WalletSweeperOptions _opts = options.Value;

    public string Name => JobName;

    /// <summary>
    /// One pass, recorded in IWalletSweeps for /healthz/wallet. A pass that throws records an attempt without a success
    /// and throws on, so the CronJob's Job fails too; the mutations stay Pending for the next pass. When the database is
    /// what failed, the record fails as well and the pass's own error is the one thrown.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sweeps = scope.ServiceProvider.GetRequiredService<IWalletSweeps>();
        int swept;
        try
        {
            swept = await SweepOnceAsync(scope.ServiceProvider.GetRequiredService<IWalletMutationRepository>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wallet sweeper pass failed");
            try
            {
                await sweeps.RecordAsync(success: false, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception recordFailure)
            {
                logger.LogError(recordFailure, "Wallet sweeper could not record its failed pass");
            }
            throw;
        }

        // A pass that found nothing still counts: finding nothing is the sweeper working.
        await sweeps.RecordAsync(success: true, DateTimeOffset.UtcNow, ct);
        if (swept > 0)
        {
            logger.LogInformation("Wallet sweeper re-drove {Count} stale mutation(s)", swept);
        }
    }

    /// <summary>
    /// Returns how many stale mutations were claimed. Public so tests run a pass through a repository of their own
    /// (WalletMutationSweeperTests).
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
                    // The status token refused to record an attempt: the row changed since GiveUpOrRetryAsync read
                    // it. On Postgres its read holds the row's lock, so only a store without row locks (InMemory)
                    // gets here. Nothing to record; the rest of the batch still runs.
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
