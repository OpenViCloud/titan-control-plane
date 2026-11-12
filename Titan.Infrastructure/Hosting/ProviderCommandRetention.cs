using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Deletes finished provider command rows older than RetentionDays, never an open one
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request). Every request writes a row of its own, so without
/// it the table grows with every click. Local DB maintenance, like IdempotencyCleanupService, not a provider command.
/// </summary>
public class ProviderCommandRetention(
    IProviderCommandRepository commands,
    IOptions<ProviderCommandRetentionOptions> options,
    TimeProvider clock,
    ILogger<ProviderCommandRetention> logger) : IScheduledJob
{
    public const string JobName = "provider-command-retention";

    public string Name => JobName;

    public Task RunAsync(CancellationToken ct) =>
        RunOnceAsync(commands, options.Value.RetentionDays, clock.GetUtcNow(), logger, ct);

    public static async Task<int> RunOnceAsync(
        IProviderCommandRepository commands, int retentionDays, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        int removed = await commands.DeleteFinishedCreatedBeforeAsync(now.AddDays(-retentionDays), ct);
        if (removed > 0)
        {
            logger.LogInformation("Provider command retention removed {Count} finished rows older than {Days} days",
                removed, retentionDays);
        }
        return removed;
    }
}
