// Same namespace as IWalletSweeps on purpose (titan-ops architecture/code-structure.md#services-branch).

namespace Titan.Application.Services.Billing.WalletMutations;

/// <param name="RegisteredAt">When the sweeper's row was written (its migration) — the reference point while it has never swept.</param>
/// <param name="LastSuccessAt">Last pass that finished without throwing; null until the first success.</param>
/// <param name="LastAttemptAt">Last pass, success or not; null until the first pass.</param>
public sealed record WalletSweepState(
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt);
