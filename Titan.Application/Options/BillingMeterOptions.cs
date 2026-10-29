namespace Titan.Application.Options;

/// <summary>
/// Tick cadence for the append-only usage meters. Prices are not here: every rate lives on its offering
/// row (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering), so changing a price is not a redeploy.
/// </summary>
public class BillingMeterOptions
{
    public const string SectionName = "BillingMeter";

    /// <summary>
    /// Also the width of each usage record's bucket: billing is approximate by design, and this is the
    /// width of the approximation. A missed tick under-bills, never over-bills.
    /// </summary>
    public int IntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Tick intervals a meter may go without a successful tick before /healthz/meters reports it
    /// Unhealthy. Default 3 (~30 minutes at the default interval): long enough to ride out one failed
    /// pass, short enough to hear about a dead meter before a period's usage goes unrecorded.
    /// </summary>
    public int MeterStaleFactor { get; set; } = 3;

}
