using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Wallet;

/// One row per tenant (titan-ops architecture/money-and-wallet.md#wallet-one-balance). The balance is stored, not
/// derived, and must equal the sum of the wallet's WalletTransaction rows; both are written in one SaveChanges.
/// Credit and Debit are the only ways it changes (titan-ops architecture/money-and-wallet.md#wallet-invariant-in-aggregate),
/// internal so that WalletLedger, which adds the row, is the only caller. The aggregate owns the non-negative rule,
/// Version owns the race.
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

    /// Opened by TenantActivation, which looks for the wallet under the tenant's row lock, so two activations take
    /// turns; UNIQUE(tenant_id) still refuses a second wallet whatever opens it.
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
    internal void Credit(long amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A wallet credit must be a positive amount.");

        AvailableAmount += amount;
        Touch();
    }

    /// The non-negative invariant lives here, whoever is asking. InvoiceWalletPayment's balance read only gives a
    /// friendlier "short by X"; this is the guard, and with Version it holds against a stale read.
    internal void Debit(long amount)
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
