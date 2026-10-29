using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Messaging;

namespace Titan.Tests.Services;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the subscriber against a fake bus.
// Silent about whether NATS itself delivers: there is no Testcontainers NATS in CI.
public class WalletMutationSubscriberTests
{
    // Records what ApplyAsync was called with, in order.
    private sealed class RecordingConsumer : IWalletMutationConsumer
    {
        public List<Guid> Applied { get; } = [];
        public Task ApplyAsync(Guid mutationId, CancellationToken ct = default)
        {
            Applied.Add(mutationId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingConsumer : IWalletMutationConsumer
    {
        public Task ApplyAsync(Guid mutationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("apply blew up");
    }

    // Yields the given notifications then completes — the fake bus.
    private sealed class FakeSubscription(params WalletMutationNotification[] items) : IWalletMutationSubscription
    {
        public async IAsyncEnumerable<WalletMutationNotification> ListenAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    [Fact] // per-message: the notification's mutation id is what gets applied
    public async Task ApplyOne_InvokesConsumer_WithTheMutationId()
    {
        var consumer = new RecordingConsumer();
        var n = new WalletMutationNotification(Guid.NewGuid(), Guid.NewGuid());

        await WalletMutationSubscriber.ApplyOneAsync(consumer, n, NullLogger.Instance, default);

        Assert.Equal([n.MutationId], consumer.Applied);
    }

    [Fact] // one bad mutation must NOT tear down the loop — it is swallowed for the sweeper
    public async Task ApplyOne_SwallowsConsumerFailure_DoesNotThrow()
    {
        var n = new WalletMutationNotification(Guid.NewGuid(), Guid.NewGuid());

        var ex = await Record.ExceptionAsync(() =>
            WalletMutationSubscriber.ApplyOneAsync(new ThrowingConsumer(), n, NullLogger.Instance, default));

        Assert.Null(ex);
    }

    [Fact] // the whole loop: fake bus → subscriber → consumer, each notification applied once, IN ORDER
    public async Task Subscriber_AppliesEveryNotification_InOrder_ThroughAScope()
    {
        var recording = new RecordingConsumer();
        var services = new ServiceCollection();
        services.AddScoped<IWalletMutationConsumer>(_ => recording);
        await using var provider = services.BuildServiceProvider();

        var n1 = new WalletMutationNotification(Guid.NewGuid(), Guid.NewGuid());
        var n2 = new WalletMutationNotification(Guid.NewGuid(), Guid.NewGuid());
        var subscriber = new WalletMutationSubscriber(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeSubscription(n1, n2),
            NullLogger<WalletMutationSubscriber>.Instance);

        await subscriber.StartAsync(default);   // ExecuteAsync runs; FakeSubscription completes the stream
        await subscriber.ExecuteTask!;          // wait for the loop to drain both notifications

        Assert.Equal([n1.MutationId, n2.MutationId], recording.Applied);
    }

    [Fact] // NATS off: publishing is a safe no-op (the mutation is durable; the sweeper drives it)
    public async Task NullPublisher_PublishIsNoOp_AndDoesNotThrow()
    {
        var publisher = new NullWalletMutationPublisher(NullLogger<NullWalletMutationPublisher>.Instance);

        var ex = await Record.ExceptionAsync(() =>
            publisher.PublishAsync(new WalletMutationNotification(Guid.NewGuid(), Guid.NewGuid())));

        Assert.Null(ex);
    }

    // The JSON property names ARE the wire format: NatsWalletMutationBus serializes this record with
    // JsonSerializer, so renaming a property renames a field on a live bus. The other tests pass the
    // record between fakes, which can never disagree with themselves; this one asserts the serialized text.
    //
    // A rename is safe to ship only because the control-plane deploys replicas: 1 with strategy: Recreate
    // (base/control-plane/deployment.yaml of OpenViCloud/titan-local-cluster), so two versions never share the subject. Under
    // RollingUpdate a subscriber reading the old name would deserialize Guid.Empty, find no row and
    // return silently — a delayed apply (the sweeper re-drives it), but nothing logs an error.
    [Fact]
    public void Notification_WireFormat_UsesMutationId()
    {
        var mutationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var walletId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var json = System.Text.Json.JsonSerializer.Serialize(
            new WalletMutationNotification(mutationId, walletId));

        Assert.Contains("\"MutationId\":\"11111111-1111-1111-1111-111111111111\"", json);
        Assert.DoesNotContain("CommandId", json);
    }
}
