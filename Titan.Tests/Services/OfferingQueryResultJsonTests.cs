using System.Text.Json;
using Titan.Application.Queries.Catalog.VmOffering.Contracts;
using Titan.Application.Queries.Catalog.VolumeOffering.Contracts;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.VolumeOffering;

namespace Titan.Tests.Services;

// Postgres returns a decimal(18,10) price with its ten decimals ("170.0000000000"); the CLI and the Terraform provider
// print the JSON number as it comes, so the price lists write the shortest form of the same value
// (titan-ops features/catalog/design.md#whole-vnd-prices).
public class OfferingQueryResultJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("170.0000000000", "170")]
    [InlineData("1.5000000000", "1.5")]
    [InlineData("1.6438356164", "1.6438356164")]
    public void AVmPrice_IsWrittenWithoutTrailingZeros(string stored, string written)
    {
        var offering = VmOffering.Create("s", "Small", 1, 2048, decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture));

        var json = JsonSerializer.Serialize(VmOfferingQueryResult.From(offering), Web);

        Assert.Contains($"\"pricePerHour\":{written}}}", json);
    }

    [Theory]
    [InlineData("2.0000000000", "2")]
    [InlineData("1.5791400000", "1.57914")]
    public void AVolumePrice_IsWrittenWithoutTrailingZeros(string stored, string written)
    {
        var offering = VolumeOffering.Create("standard", "Standard", decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture));

        var json = JsonSerializer.Serialize(VolumeOfferingQueryResult.From(offering), Web);

        Assert.Contains($"\"pricePerGbPerHour\":{written}}}", json);
    }
}
