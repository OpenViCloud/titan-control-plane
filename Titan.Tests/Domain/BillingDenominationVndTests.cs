using System.IO;
using Titan.Application.Options;
using Titan.Application.Services.Billing;

namespace Titan.Tests.Domain;

// Billing is denominated in VND (titan-ops architecture/money-and-wallet.md#money-is-long-vnd).
public class BillingDenominationVndTests
{
    [Fact] // the one source meters and billing handlers read the currency from
    public void DefaultCurrency_IsVnd() =>
        Assert.Equal("VND", BillingConstants.DefaultCurrency);

    [Fact]
    public void CreditOptions_DefaultsToVnd() =>
        Assert.Equal("VND", new CreditOptions().Currency);

    // Prices belong to their offerings (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering), and
    // their VND magnitude is pinned on the seed in OfferingPricingSchemaTests. This guards that no price is in config.
    [Fact]
    public void BillingMeterOptions_CarriesNoPrices()
    {
        var priceProps = typeof(BillingMeterOptions).GetProperties()
            .Where(p => p.Name.Contains("Price", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(priceProps);
    }

    // Comment lines are skipped; this proves the "SGD" label is gone from code, not just that "VND" appears
    // somewhere. Migrations are immutable and out of scope.
    [Fact]
    public void NoLiveSgdLiteral_InApplicationOrDomain()
    {
        var root = FindRepoDir();
        string[] dirs =
        {
            Path.Combine(root, "Titan.Application"),
            Path.Combine(root, "Titan.Domain"),
        };

        var offenders = new List<string>();
        foreach (var dir in dirs)
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var lineNo = 0;
            foreach (var raw in File.ReadLines(file))
            {
                lineNo++;
                var line = raw.TrimStart();
                if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*"))
                    continue; // a comment may still mention SGD
                if (raw.Contains("\"SGD\""))
                    offenders.Add($"{Path.GetFileName(file)}:{lineNo}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Live \"SGD\" literal(s) remain in the money path: " + string.Join(", ", offenders));
    }

    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Application")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
