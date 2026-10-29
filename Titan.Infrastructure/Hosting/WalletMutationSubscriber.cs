using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.WalletMutations;

namespace Titan.Infrastructure.Hosting;

/// The single writer per wallet only because control-plane runs at replicas: 1, with no leader election
/// (titan-ops architecture/money-and-wallet.md#single-writer-outbox). Registered only when NatsOptions.IsActive.
public class WalletMutationSubscriber(
    IServiceScopeFactory scopeFactory,
    IWalletMutationSubscription subscription,
    ILogger<WalletMutationSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Wallet mutation subscriber started; listening for mutation notifications");
        try
        {
            await foreach (var notification in subscription.ListenAsync(stoppingToken))
            {
                // A fresh scope per notification = a fresh DbContext per mutation.
                using var scope = scopeFactory.CreateScope();
                var consumer = scope.ServiceProvider.GetRequiredService<IWalletMutationConsumer>();
                await ApplyOneAsync(consumer, notification, logger, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The subscription faulted (NATS dropped, say). The sweeper keeps mutations moving meanwhile, so
            // this logs rather than crashes the host.
            logger.LogError(ex,
                "Wallet mutation subscription faulted; mutations will be driven by the sweeper until it recovers");
        }
    }

    /// A failure to apply one mutation is swallowed on purpose: it stays Pending for the sweeper, whereas an
    /// escaping exception would tear down the listen loop and strand every later notification.
    public static async Task ApplyOneAsync(
        IWalletMutationConsumer consumer, WalletMutationNotification notification, ILogger logger, CancellationToken ct)
    {
        try
        {
            await consumer.ApplyAsync(notification.MutationId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to apply wallet mutation {MutationId} (wallet {WalletId}); leaving it for the sweeper",
                notification.MutationId, notification.WalletId);
        }
    }
}
