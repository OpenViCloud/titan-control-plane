using Microsoft.Extensions.Logging;
using Titan.Application.Services.Billing.Metering;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Ticks every registered <see cref="IMeter"/> once (titan-ops architecture/money-and-wallet.md#append-only-metering) when
/// the meter's CronJob calls. Each meter has its own try/catch, so one throwing does not stop the others; every attempt is
/// recorded in <see cref="IMeterTicks"/> for /healthz/meters, and a pass with a failed meter fails, so the CronJob's Job
/// shows it too. A record per resource per bucket keeps a second pass in the same bucket from billing twice.
/// </summary>
public class MeterJob(IEnumerable<IMeter> meters, IMeterTicks ticks, ILogger<MeterJob> logger) : IScheduledJob
{
    public const string JobName = "meter";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (IMeter meter in meters)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool success;
            try
            {
                await meter.RunTickAsync(now, ct);
                success = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Meter} meter tick failed", meter.Name);
                success = false;
                failed.Add(meter.Name);
            }
            await ticks.RecordAsync(meter.Name, success, now, ct);
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException($"meters failed: {string.Join(", ", failed)}");
        }
    }
}
