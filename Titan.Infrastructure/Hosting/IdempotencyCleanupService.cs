using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Without a TTL idempotency_keys grows unbounded. A scheduled job, not a provider command: this is local DB maintenance,
/// not a business workflow.
/// </summary>
public class IdempotencyCleanupService(
    IIdempotencyStore store,
    IOptions<IdempotencyCleanupOptions> options,
    ILogger<IdempotencyCleanupService> logger) : IScheduledJob
{
    public const string JobName = "idempotency-cleanup";

    private readonly IdempotencyCleanupOptions _opts = options.Value;

    public string Name => JobName;

    public async Task RunAsync(CancellationToken ct)
    {
        int removed = await PurgeOnceAsync(store, ct);
        if (removed > 0)
        {
            logger.LogInformation("Idempotency cleanup removed {Count} expired records", removed);
        }
    }

    /// <summary>Public for testing.</summary>
    public Task<int> PurgeOnceAsync(IIdempotencyStore idempotency, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-_opts.TtlHours);
        return idempotency.PurgeExpiredAsync(cutoff, ct);
    }
}
