namespace Titan.Application.Services.Billing.Metering;

/// <summary>
/// One append-only sampling strategy, ticked by the meter's scheduled job, Titan.Infrastructure.Hosting.MeterJob
/// (titan-ops architecture/money-and-wallet.md#append-only-metering).
/// </summary>
public interface IMeter
{
    /// <summary>
    /// The key in <see cref="IMeterTicks"/> (a row of core.meter_ticks, written by the migration that adds the meter)
    /// and the liveness health check. Closed set {Compute, Disk, Snapshot}, guarded by MeterRegistrationTests.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Appends usage records for the bucket containing <paramref name="now"/>. Idempotent per
    /// (resource, bucket). Returns how many new records were inserted.
    /// </summary>
    Task<int> RunTickAsync(DateTimeOffset now, CancellationToken ct);
}
