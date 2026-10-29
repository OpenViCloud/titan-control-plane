namespace Titan.Application.Options;

// Settings for admin-granted promotional credit: the admin types the amount on each grant, and the cap
// guards against a typo (500.000 vs 50.000.000).
public class CreditOptions
{
    public const string SectionName = "Credit";

    /// Upper bound for a single grant (VND). A sane ceiling, not an invariant: the handler refuses a larger
    /// grant with GRANT_AMOUNT_EXCEEDS_MAX.
    public decimal MaxGrantAmount { get; set; } = 100_000_000m;

    /// Currency of the grant — must match the billing default (VND, titan-ops architecture/money-and-wallet.md#money-is-long-vnd) so credit and usage are comparable.
    public string Currency { get; set; } = "VND";
}
