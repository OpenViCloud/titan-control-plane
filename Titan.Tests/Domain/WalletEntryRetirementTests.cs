using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

/// <summary>
/// The retired lot model (WalletEntry) must be DEAD in production code
/// (titan-ops architecture/money-and-wallet.md#wallet-one-balance). "The balance still matches" passes even if the
/// one-balance wallet is never consulted, as long as the old model is still there answering.
///
/// They scan the SOURCE TREE, not the loaded assembly, on purpose: a method nobody calls still exists
/// at runtime, and a repository that still selects remaining_amount still compiles. Reflection would
/// report a clean model while the old query kept serving traffic.
///
/// The migration folder is exempt: migrations are a record of what the schema once was.
/// </summary>
public class WalletEntryRetirementTests
{
    private static readonly string[] ProductionProjects =
        ["Titan.Domain", "Titan.Application", "Titan.Infrastructure", "Titan.Host"];

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    /// Every production .cs file except migrations, which are a record of what the schema once was.
    private static IEnumerable<(string Path, string Text)> ProductionSources() =>
        ProductionProjects
            .Select(p => Path.Combine(SolutionRoot(), p))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (f, StripComments(File.ReadAllText(f))));

    /// Comments are stripped first, so these tests measure CODE, not prose that names the retired model.
    ///
    /// String literals are deliberately NOT stripped: the SQL that reads the old column lives in one,
    /// and that is the single most important thing here to catch.
    /// `//` preceded by `:` is left alone so a URL inside a literal cannot swallow the rest of its line.
    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline),
                      @"(?<!:)//[^\n]*", " ");

    private static void AssertNoProductionCodeMentions(string pattern, string why)
    {
        var sources = ProductionSources().ToList();

        // Harness self-check: a scan over nothing finds nothing. If the file walk broke, these tests
        // would go green while measuring an empty set — the exact false PASS they exist to prevent.
        Assert.True(sources.Count > 50, $"Only {sources.Count} source files scanned — the walk is broken.");

        var offenders = sources
            .Where(s => Regex.IsMatch(s.Text, pattern))
            .Select(s => Path.GetFileName(s.Path))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{why}{Environment.NewLine}Still referenced in: {string.Join(", ", offenders)}");
    }

    // The pattern requires the type to be used as an identifier, not merely named.
    [Fact]
    public void N1_NoProductionCodeUsesTheWalletEntryType() =>
        AssertNoProductionCodeMentions(
            @"\bWalletEntry\s*[\.\(\)<>,;\[\]]|\bWalletEntry\s+\w|\bWalletEntrySource\b",
            "WalletEntry was retired by docs/architecture/money-and-wallet.md#wallet-one-balance; the wallet is one balance plus a signed ledger.");

    // THE assertion of this file: while any query still
    // selects remaining_amount, the old wallet is alive and every "the numbers match" test is
    // measuring it rather than the new one.
    [Fact]
    public void N2_NoProductionCodeReadsRemainingAmount() =>
        AssertNoProductionCodeMentions(
            @"remaining_amount|RemainingAmount",
            "The balance now lives in wallets.available_amount; reading remaining_amount means the old wallet is still answering.");

    // Gone, not merely unused: these repository methods only made sense for lots.
    [Fact]
    public void N3_NoProductionCodeUsesTheLotRepositoryMethods() =>
        AssertNoProductionCodeMentions(
            @"GetConsumableEntriesAsync|ListEntriesByTenantAsync|\bAddEntry\b",
            "Paying an invoice no longer selects lots to draw down, so nothing should be able to ask for them.");

    // The draw-down plan described "take this much from that lot", a sentence the one-balance wallet cannot say.
    [Fact]
    public void N4_NoProductionCodeUsesCreditDrawdown() =>
        AssertNoProductionCodeMentions(
            @"\bCreditDrawdown\b",
            "There is nothing to draw down from: a wallet is one row, debited by one conditional UPDATE.");
}
