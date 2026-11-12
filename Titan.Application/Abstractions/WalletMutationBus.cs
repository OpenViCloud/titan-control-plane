namespace Titan.Application.Abstractions;

/// Published after a wallet mutation is durably enqueued (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
/// It carries no money and no authority: the consumer re-loads the mutation from the DB, so a lost
/// notify is a delayed apply and the bus may be non-persistent.
///
/// THESE PROPERTY NAMES ARE THE WIRE FORMAT: NatsWalletMutationBus serializes this record with
/// JsonSerializer. A rename is safe only in a release that deploys the control plane with Recreate, so two
/// versions never publish onto one subject (titan-ops architecture/platform-infra.md#control-plane-replicas). Under a
/// rolling update it becomes a silent failure: the subscriber deserializes Guid.Empty, finds no row and returns
/// without logging.
/// Change a name here only alongside a compatibility plan
/// (WalletMutationSubscriberTests.Notification_WireFormat_UsesMutationId pins the current one).
public record WalletMutationNotification(Guid MutationId, Guid WalletId);

/// Publishes after the mutation's transaction commits (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
/// A no-op implementation runs when NATS is not configured, which is safe because the mutation is
/// already durable and the sweeper covers a missing notify.
public interface IWalletMutationPublisher
{
    Task PublishAsync(WalletMutationNotification notification, CancellationToken ct = default);
}

/// Only the NATS implementation provides it; when NATS is off no subscriber is registered, so this is
/// never resolved. Separate from the publisher: the enqueuers only publish.
public interface IWalletMutationSubscription
{
    /// Yields notifications as they arrive, in per-subject (per-wallet) order, until cancelled.
    IAsyncEnumerable<WalletMutationNotification> ListenAsync(CancellationToken ct);
}
