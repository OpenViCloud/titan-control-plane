using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

/// The types that keep one status and an Operation of their own (titan-ops architecture/domain-methods.md#provider-resource)
/// are listed in test comments and in CLAUDE.md, and a list written before a type joined went stale in six places at
/// once. Five or more of their names in a row (joined by commas, "and", "và", "·" or "/") are a list of them, so the run
/// names all of them.
///
/// Silent about: a run of fewer than five (a pair, or the children only), names scattered through a sentence, and a
/// type spelled in another case ("VM" is read as Vm, because the bullets in CLAUDE.md spell it so).
public class OneStatusTypeListTests
{
    private static readonly string[] Types =
    [
        "Vm", "Network", "FirewallRule", "VmSnapshot", "Volume", "VolumeSnapshot", "VmTemplateCluster", "LoadBalancer",
    ];

    private const int ListStartsAt = 5;

    // Built from Types, so a type added there is read by the scan with no second list to remember.
    private static readonly string AnyName = @"\b(?:VM|" + string.Join("|", Types) + @")\b";

    private static readonly Regex Run = new(
        AnyName + @"(?:\s*(?:,|\s+và|\s+and|·|/)\s*" + AnyName + ")+", RegexOptions.Compiled);

    private static readonly Regex Single = new(AnyName, RegexOptions.Compiled);

    private static string[] NamesIn(string run) =>
        Single.Matches(run).Select(m => m.Value == "VM" ? "Vm" : m.Value).Distinct().ToArray();

    [Fact]
    public void ACommentOrBulletListingFiveOfTheOneStatusTypes_NamesThemAll()
    {
        List<string> stale = [];
        foreach ((string place, string text) in TestComments().Concat(ClaudeMdBullets()))
        {
            foreach (Match run in Run.Matches(text.Replace("`", "")))
            {
                string[] named = NamesIn(run.Value);
                if (named.Length >= ListStartsAt && named.Length < Types.Length)
                {
                    stale.Add($"{place} misses {string.Join(", ", Types.Except(named))}");
                }
            }
        }

        Assert.True(stale.Count == 0,
            "These lists of the one-status types are missing a type; add it, or take the list below five names:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void TheScan_FindsTheListsItGuards()
    {
        // A scan that finds nothing is green too: the lists in the guards below the Domain tests name them all.
        int lists = TestComments().Concat(ClaudeMdBullets())
            .SelectMany(entry => Run.Matches(entry.Text.Replace("`", "")).Select(run => NamesIn(run.Value)))
            .Count(named => named.Length >= ListStartsAt);

        Assert.True(lists >= 3, $"Expected the existing lists to be found, found {lists}.");
    }

    [Fact]
    public void AListOfSixTypes_IsStale()
    {
        string six = "Vm, Network, FirewallRule, VmSnapshot, Volume and VolumeSnapshot";
        Match run = Run.Match(six);
        int named = NamesIn(run.Value).Length;

        Assert.True(named >= ListStartsAt && named < Types.Length);
    }

    /// Each run of consecutive comment lines is one text, so a list that wraps onto the next line is still one list.
    private static IEnumerable<(string Place, string Text)> TestComments()
    {
        string tests = Path.Combine(RepoDir(), "Titan.Tests");
        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            bool isBuildOutput = file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
            if (isBuildOutput)
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            List<string> run = [];
            int runStart = 0;
            for (int i = 0; i <= lines.Length; i++)
            {
                string? line = i < lines.Length ? lines[i].TrimStart() : null;
                bool isComment = line != null && line.StartsWith("//");
                if (isComment)
                {
                    if (run.Count == 0)
                    {
                        runStart = i + 1;
                    }
                    run.Add(line!.TrimStart('/', ' '));
                    continue;
                }
                if (run.Count > 0)
                {
                    yield return ($"{Path.GetRelativePath(RepoDir(), file)}:{runStart}", string.Join(' ', run));
                    run.Clear();
                }
            }
        }
    }

    private static IEnumerable<(string Place, string Text)> ClaudeMdBullets()
    {
        string path = Path.Combine(RepoDir(), "CLAUDE.md");
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            yield return ($"CLAUDE.md:{i + 1}", lines[i]);
        }
    }

    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
