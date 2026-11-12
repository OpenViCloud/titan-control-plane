namespace Titan.Infrastructure.Persistence;

/// One meter's ticks (Titan.Application.Services.Billing.Metering.IMeterTicks). Not a domain aggregate.
public class MeterTick
{
    public string Meter { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}
