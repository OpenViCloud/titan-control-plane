using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NATS.Net;
using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Messaging;

/// Per-wallet subject "<prefix>.<walletId>": per-subject order is per-wallet order. A plain JSON string
/// payload sidesteps NATS.Net's custom-type serializer registry. NATS core drops a message published while
/// the subscriber is down; that is acceptable only because the mutation is a durable row the sweeper
/// re-drives (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
///
/// NOTE: no Testcontainers NATS, so the round-trip against a real broker is verified only on the cluster.
public sealed class NatsWalletMutationBus : IWalletMutationPublisher, IWalletMutationSubscription, IAsyncDisposable
{
    private readonly NatsClient _client;
    private readonly NatsOptions _opts;
    private readonly ILogger<NatsWalletMutationBus> _logger;

    public NatsWalletMutationBus(NatsOptions opts, ILogger<NatsWalletMutationBus> logger)
    {
        _opts = opts;
        _logger = logger;
        _client = new NatsClient(opts.Url);
    }

    public async Task PublishAsync(WalletMutationNotification notification, CancellationToken ct = default)
    {
        var subject = SubjectFor(notification.WalletId);
        var payload = JsonSerializer.Serialize(notification);
        await _client.PublishAsync(subject, payload, cancellationToken: ct);
    }

    public async IAsyncEnumerable<WalletMutationNotification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // One subscriber to every wallet's subject; NATS delivers a single subject's messages in order.
        var subject = $"{_opts.SubjectPrefix}.*";
        await foreach (var msg in _client.SubscribeAsync<string>(subject, cancellationToken: ct))
        {
            WalletMutationNotification? notification;
            try
            {
                notification = msg.Data is null
                    ? null
                    : JsonSerializer.Deserialize<WalletMutationNotification>(msg.Data);
            }
            catch (JsonException ex)
            {
                // Dropped, not fatal: the mutation is still durable and the sweeper drives it. Killing the
                // loop would strand every later wallet.
                _logger.LogError(ex, "Dropping unparseable wallet mutation notify on subject {Subject}", msg.Subject);
                continue;
            }

            if (notification is not null) yield return notification;
        }
    }

    private string SubjectFor(Guid walletId) => $"{_opts.SubjectPrefix}.{walletId:N}";

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
