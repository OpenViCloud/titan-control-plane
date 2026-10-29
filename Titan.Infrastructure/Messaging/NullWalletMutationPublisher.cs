using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;

namespace Titan.Infrastructure.Messaging;

/// A no-op is safe: the mutation is already durable in the outbox, so a missing notify costs latency until the
/// sweeper drives it, never money (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
public class NullWalletMutationPublisher(ILogger<NullWalletMutationPublisher> logger) : IWalletMutationPublisher
{
    public Task PublishAsync(WalletMutationNotification notification, CancellationToken ct = default)
    {
        logger.LogDebug(
            "NATS disabled; not notifying for mutation {MutationId} — it is durable and the sweeper will drive it",
            notification.MutationId);
        return Task.CompletedTask;
    }
}
