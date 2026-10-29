using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — records the post-commit notifications an enqueuer publishes, so a test can assert the
/// notify fired (and for which mutation) without a real bus. Publishing never throws: a real bus error
/// is swallowed by the enqueuer anyway (the durable mutation + sweeper are the net), so the fake models
/// the happy path the test cares about.
public sealed class FakeWalletMutationPublisher : IWalletMutationPublisher
{
    public List<WalletMutationNotification> Published { get; } = [];

    /// Makes the bus fail, so a test can prove an enqueuer treats a lost notify as a delay rather
    /// than an error. A real broker outage looks exactly like this from the caller's side.
    public bool ThrowOnPublish { get; set; }

    public Task PublishAsync(WalletMutationNotification notification, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
            throw new InvalidOperationException("Bus unreachable (test).");

        Published.Add(notification);
        return Task.CompletedTask;
    }
}
