using Microsoft.EntityFrameworkCore;
using Titan.Application.Services.Billing.Metering;

namespace Titan.Infrastructure.Persistence;

/// core.meter_ticks, one row per meter; the migration that adds a meter writes its row.
public sealed class PostgresMeterTicks(TitanDbContext db) : IMeterTicks
{
    // One statement: a meter without a row (not yet migrated) gets one, registered at this attempt; a failure keeps the
    // last success.
    public Task RecordAsync(string meter, bool success, DateTimeOffset at, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO core.meter_ticks (meter, registered_at, last_attempt_at, last_success_at)
             VALUES ({meter}, {at}, {at}, CASE WHEN {success} THEN {at} END)
             ON CONFLICT (meter) DO UPDATE SET
                 last_attempt_at = EXCLUDED.last_attempt_at,
                 last_success_at = CASE WHEN {success} THEN EXCLUDED.last_attempt_at ELSE meter_ticks.last_success_at END
             """,
            ct);

    public async Task<IReadOnlyDictionary<string, MeterTickState>> SnapshotAsync(CancellationToken ct)
    {
        List<MeterTick> rows = await db.MeterTicks.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => r.Meter, r => new MeterTickState(r.RegisteredAt, r.LastSuccessAt, r.LastAttemptAt));
    }
}
