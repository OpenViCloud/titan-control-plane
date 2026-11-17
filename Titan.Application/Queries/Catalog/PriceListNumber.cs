using System.Globalization;

namespace Titan.Application.Queries.Catalog;

// A price column is decimal(18,10), so Postgres returns 170 as 170.0000000000 and the JSON number carries the ten
// zeros to the CLI and the Terraform provider, which print it as it comes
// (titan-ops features/catalog/design.md#whole-vnd-prices). Same value, shortest form; no rounding.
public static class PriceListNumber
{
    public static decimal WithoutTrailingZeros(decimal price) =>
        decimal.Parse(price.ToString("0.##########", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
