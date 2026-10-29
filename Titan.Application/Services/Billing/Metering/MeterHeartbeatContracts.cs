// Same namespace as MeterHeartbeat on purpose (titan-ops architecture/code-structure.md#services-branch).

namespace Titan.Application.Services.Billing.Metering;

/// <param name="RegisteredAt">When the meter was first declared — the reference point for the boot grace window.</param>
/// <param name="LastSuccessAt">Last tick that inserted/committed without throwing; null until the first success.</param>
/// <param name="LastAttemptAt">Last tick attempt, success or not; null until the first attempt.</param>
public sealed record MeterTickState(
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt);
