using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Wallet;

/// One row per tenant (titan-ops architecture/money-and-wallet.md#wallet-one-balance). The balance is stored, not
/// derived, and must equal the sum of the wallet's WalletTransaction rows; both are written in one SaveChanges.
/// Credit and Debit are the only ways it changes (titan-ops architecture/money-and-wallet.md#wallet-invariant-in-aggregate):
/// the aggregate owns the non-negative rule, Version owns the race.
public class Wallet : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Currency { get; private set; } = default!;

    /// Whole VND. Never negative because Debit refuses, not because of the type.
    public long AvailableAmount { get; private set; }

    /// Concurrency token (titan-ops architecture/money-and-wallet.md#version-token), bumped by every balance change:
    /// two writers who read the same balance cannot both commit.
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Wallet() { }

    /// Opened at tenant activation. UNIQUE(tenant_id) keeps a racing pair of openers from producing two
    /// wallets; a check-then-insert in C# would be a race, not a guard.
    public static Wallet Open(Guid tenantId, string currency)
    {
        var now = DateTimeOffset.UtcNow;
        return new Wallet
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Currency = currency,
            AvailableAmount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// Positive only: a negative credit would be a spend wearing the wrong sign.
    public void Credit(long amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A wallet credit must be a positive amount.");

        AvailableAmount += amount;
        Touch();
    }

    /// The non-negative invariant lives here, whoever is asking. The pay handler's balance read only gives a
    /// friendlier "short by X"; this is the guard, and with Version it holds against a stale read.
    public void Debit(long amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A wallet debit must be a positive amount.");
        if (AvailableAmount < amount)
            throw new BusinessException("WALLET_INSUFFICIENT_FUNDS",
                $"Wallet balance {AvailableAmount} cannot cover {amount}.");

        AvailableAmount -= amount;
        Touch();
    }

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}
