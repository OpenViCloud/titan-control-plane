using Titan.Domain.Exceptions;

namespace Titan.Domain.Values;

/// A price per unit of time: VND per hour, or VND per GB per hour
/// (titan-ops architecture/money-and-wallet.md#rate-value-object). A price is not a whole number of đồng, so it
/// stays a decimal; there is no conversion to long and no Rate + Rate, so a price cannot be mistaken for a
/// balance (MoneyAndRateTests).
public readonly struct Rate : IEquatable<Rate>
{
    public decimal PerUnit { get; private init; }

    /// Zero is a valid price (a free offering). Also runs when EF materialises a row, so a negative price in
    /// the database fails loudly at load instead of being served.
    public static Rate Of(decimal perUnit)
    {
        if (perUnit < 0m)
            throw new BusinessException("INVALID_RATE", "A price cannot be negative.");
        return new Rate { PerUnit = perUnit };
    }

    /// A raw, unrounded VND amount. UsageRecord.TotalVnd rounds once, on the sum of an invoice line, so a sub-đồng
    /// price is not rounded away before it is scaled and summed.
    public static decimal operator *(Rate rate, decimal hours) => rate.PerUnit * hours;

    public static bool operator ==(Rate a, Rate b) => a.PerUnit == b.PerUnit;
    public static bool operator !=(Rate a, Rate b) => a.PerUnit != b.PerUnit;

    public bool Equals(Rate other) => PerUnit == other.PerUnit;
    public override bool Equals(object? obj) => obj is Rate r && Equals(r);
    public override int GetHashCode() => PerUnit.GetHashCode();
    public override string ToString() => PerUnit.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
