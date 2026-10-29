using System.Reflection;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// Money is a bare `long` VND (titan-ops architecture/money-and-wallet.md#money-is-long-vnd); Rate stays a value object
// (titan-ops architecture/money-and-wallet.md#rate-value-object) because a price is fractional. The risk is a Rate
// leaking into integer money arithmetic, so Rate refuses a negative, multiplies by hours to a RAW decimal (the
// meter rounds, not Rate), and offers no conversion and no additive operator.
public class MoneyAndRateTests
{
    [Fact]
    public void Rate_CarriesItsAmount_AndComparesByValue()
    {
        Assert.Equal(1.579m, Rate.Of(1.579m).PerUnit);
        Assert.Equal(Rate.Of(1.579m), Rate.Of(1.579m));
        Assert.NotEqual(Rate.Of(1.579m), Rate.Of(1.58m));
    }

    // A price cannot be negative — there is no ledger reading that makes it sensible.
    [Fact]
    public void Rate_RefusesNegative()
    {
        Assert.Equal("INVALID_RATE", Assert.Throws<BusinessException>(() => Rate.Of(-1m)).Code);
    }

    [Fact]
    public void Rate_OfZero_IsAllowed()
    {
        // A free offering is a real thing; 0 is a price, not an error.
        Assert.Equal(0m, Rate.Of(0m).PerUnit);
    }

    // UsageRecord.TotalVnd (one invoice line) is the single place that rounds to whole VND. The expected value is a
    // LITERAL: recomputing `18m * (2/60m)` on the right would pass even if Rate started rounding, because both
    // sides would round together. 2/60m repeats, so an unrounded result is 0.5999999999999999999999999994.
    [Fact]
    public void Rate_TimesHours_IsRawDecimal_AndIntroducesNoRounding()
    {
        decimal amount = Rate.Of(18m) * (2 / 60m);
        Assert.Equal(0.5999999999999999999999999994m, amount);
    }

    // ── titan-ops architecture/money-and-wallet.md#money-is-long-vnd: the two must not mix ────────────────────────────────────────────────────
    //
    // These are COMPILE-time facts, pinned by reflection: if someone adds an implicit/explicit conversion on
    // Rate, or a `Rate + x` operator, one of these goes red.
    //
    //   long balance = 100; var r = Rate.Of(5m);
    //   balance + r   // CS0019 — no operator
    //   r + r         // CS0019 — adding two prices is meaningless
    //   (long)r       // CS0030 — no conversion; use .PerUnit, which says what it is

    [Fact]
    public void Rate_OffersNoConversionOperator()
    {
        var conversions = typeof(Rate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Select(m => $"Rate.{m.Name} -> {m.ReturnType.Name}")
            .ToList();
        Assert.True(conversions.Count == 0,
            "Rate exposes a conversion operator, which re-opens the mixing docs/architecture/money-and-wallet.md#money-is-long-vnd's R1 guards against: "
            + string.Join(", ", conversions));
    }

    [Fact]
    public void Rate_OffersNoAdditiveOperator()
    {
        var adders = typeof(Rate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Addition" or "op_Subtraction")
            .Select(m => m.Name)
            .ToList();
        Assert.True(adders.Count == 0,
            "Rate gained an additive operator. Adding two prices, or a price to a balance, is the "
            + "exact confusion this guard exists to make impossible: " + string.Join(", ", adders));
    }

    // The only product Rate offers is `Rate * decimal`, and it returns a decimal — never a Rate, so
    // `rate * hours * more` cannot accidentally stay a price.
    [Fact]
    public void Rate_TimesDecimal_ReturnsDecimal_NotRate()
    {
        var mul = typeof(Rate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "op_Multiply");
        Assert.Equal(typeof(decimal), mul.ReturnType);
    }
}
