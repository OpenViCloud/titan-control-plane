namespace Titan.Application.Services.Billing.Metering;

/// <summary>
/// Each meter's last attempt and last success, written by the meter's scheduled job and read by /healthz/meters: a meter
/// whose ticks all fail records nothing and raises no visible error while the invoice silently under-bills. Kept where
/// every control plane pod reads the same answer, whichever pod the job ran on
/// (titan-ops features/billing/metering/design.md#meter-liveness).
/// </summary>
public interface IMeterTicks
{
    Task RecordAsync(string meter, bool success, DateTimeOffset at, CancellationToken ct);

    /// Every registered meter, by name.
    Task<IReadOnlyDictionary<string, MeterTickState>> SnapshotAsync(CancellationToken ct);
}
