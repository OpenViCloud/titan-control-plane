using Microsoft.EntityFrameworkCore;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Persistence;

/// The row of core.wallet_sweeps (titan-ops features/billing/wallet/design.md#healthz-wallet).
public sealed class PostgresWalletSweeps(TitanDbContext db) : IWalletSweeps
{
    // One statement: without the row (not yet migrated) the pass writes one, registered at this attempt; a failure keeps
    // the last success.
    public Task RecordAsync(bool success, DateTimeOffset at, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO core.wallet_sweeps (job, registered_at, last_attempt_at, last_success_at)
             VALUES ({WalletMutationSweeper.JobName}, {at}, {at}, CASE WHEN {success} THEN {at} END)
             ON CONFLICT (job) DO UPDATE SET
                 last_attempt_at = EXCLUDED.last_attempt_at,
                 last_success_at = CASE WHEN {success} THEN EXCLUDED.last_attempt_at ELSE wallet_sweeps.last_success_at END
             """,
            ct);

    public async Task<WalletSweepState?> GetAsync(CancellationToken ct)
    {
        WalletSweep? row = await db.WalletSweeps.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Job == WalletMutationSweeper.JobName, ct);
        if (row == null)
        {
            return null;
        }
        return new WalletSweepState(row.RegisteredAt, row.LastSuccessAt, row.LastAttemptAt);
    }
}
