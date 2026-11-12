// Same namespace as IMeterTicks on purpose (titan-ops architecture/code-structure.md#services-branch).

namespace Titan.Application.Services.Billing.Metering;

/// <param name="RegisteredAt">When the meter's row was written (the migration that added it) — the reference point while it has never ticked.</param>
/// <param name="LastSuccessAt">Last tick that inserted/committed without throwing; null until the first success.</param>
/// <param name="LastAttemptAt">Last tick attempt, success or not; null until the first attempt.</param>
public sealed record MeterTickState(
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt);
