using System.Security.Cryptography;
using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Billing.TopUp;

/// Any credit into the wallet (titan-ops features/billing/topup/design.md#topup-lifecycle). A Confirmed TopUp is the
/// customer's receipt (titan-ops architecture/money-and-wallet.md#confirmed-topup-is-receipt).
public class TopUp
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// Whole VND, positive (guarded in the factories).
    public long Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public TopUpStatus Status { get; private set; }

    /// Titan's reconciliation code (TITAN-XXXXXXXX), distinct from the bank's PaymentTxnRef. The customer's
    /// bank transfer and the SePay webhook reference it.
    public string PaymentRef { get; private set; } = default!;

    /// Null while Pending.
    public TopUpSource? Source { get; private set; }

    /// The gateway's own transaction id, for audit. Null while Pending.
    public string? PaymentTxnRef { get; private set; }
    public string? FailureReason { get; private set; }

    /// Why an admin granted free credit — audit only; null for a real-money top-up.
    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public string? ConfirmedBy { get; private set; }

    private TopUp() { }

    public static TopUp Create(Guid tenantId, long amount, string currency)
    {
        if (amount <= 0) throw new ArgumentException("amount must be > 0", nameof(amount));

        return new TopUp
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Amount = amount,
            Currency = currency,
            Status = TopUpStatus.Pending,
            PaymentRef = GeneratePaymentRef(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// Born Confirmed: there is no payment to wait for, so nothing for the SePay webhook or Confirm() to act on
    /// later. PaymentRef is still generated as Titan's id for the movement, not a claim that a bank transfer
    /// exists (titan-ops features/billing/topup/design.md#promo-grant).
    public static TopUp GrantPromo(Guid tenantId, long amount, string currency, string? reason, string grantedBy)
    {
        if (amount <= 0) throw new ArgumentException("amount must be > 0", nameof(amount));

        var now = DateTimeOffset.UtcNow;
        return new TopUp
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Amount = amount,
            Currency = currency,
            Status = TopUpStatus.Confirmed,
            Source = TopUpSource.PromotionalCredit,
            PaymentRef = GeneratePaymentRef(),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ConfirmedBy = grantedBy,
            CreatedAt = now,
            ConfirmedAt = now
        };
    }

    /// Only from Pending: the domain half of no-double-credit. confirmedAt lets the SePay webhook stamp the
    /// bank's transaction date rather than the moment the webhook was processed.
    public void Confirm(TopUpSource source, string confirmedBy, string? paymentTxnRef, DateTimeOffset? confirmedAt = null)
    {
        if (Status != TopUpStatus.Pending)
            throw new BusinessException("TOPUP_INVALID_STATE",
                $"Only a pending top-up can be confirmed (was {Status}).");

        Status = TopUpStatus.Confirmed;
        Source = source;
        ConfirmedBy = confirmedBy;
        PaymentTxnRef = paymentTxnRef;
        ConfirmedAt = confirmedAt ?? DateTimeOffset.UtcNow;
    }

    public void Fail(string reason)
    {
        if (Status != TopUpStatus.Pending)
            throw new BusinessException("TOPUP_INVALID_STATE",
                $"Only a pending top-up can fail (was {Status}).");

        Status = TopUpStatus.Failed;
        FailureReason = reason;
    }

    // Crockford base32 (no I/L/O/U): unambiguous when a human reads it off a bank transfer. Uniqueness is
    // enforced by the DB index; 40 random bits only make collisions rare.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string GeneratePaymentRef()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;

        Span<char> chars = stackalloc char[8];
        for (int i = 7; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return "TITAN-" + new string(chars);
    }
}
