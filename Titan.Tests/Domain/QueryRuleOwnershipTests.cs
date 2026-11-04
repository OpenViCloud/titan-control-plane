using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

/// What counts toward a tenant's quota and what is billed is a rule of the Domain, written once as an expression on
/// the aggregate or child (Vm.OccupiesQuota, Vm.IsBilled, …); the queries only apply it
/// (titan-ops features/vm/design.md#counting-and-billing). A repository or fake that writes the state condition again
/// can drift from the rule without any test seeing it.
///
/// It scans the SOURCE, not the assembly: an expression the repository no longer calls still compiles.
/// Silent about: a query of another name that decides quota or billing, and a query of a listed name outside the two
/// scanned folders (an inline test double, such as the throwing ones in AdminResourceServiceTests).
public class QueryRuleOwnershipTests
{
    // (file, method) of every query that answers "does it count toward the quota" or "is it billed".
    private static readonly (string File, string Method)[] RuleQueries =
    [
        ("Titan.Infrastructure/Repositories/VmRepository.cs", "CountActiveByTenantAsync"),
        ("Titan.Infrastructure/Repositories/VmRepository.cs", "ListBillableAsync"),
        ("Titan.Infrastructure/Repositories/VolumeRepository.cs", "CountActiveByTenantAsync"),
        ("Titan.Infrastructure/Repositories/VolumeRepository.cs", "ListBillableAsync"),
        ("Titan.Infrastructure/Repositories/NetworkRepository.cs", "CountActiveByTenantAsync"),
        ("Titan.Infrastructure/Repositories/NetworkRepository.cs", "CountPublicIpv6ActiveByTenantAsync"),
        ("Titan.Infrastructure/Repositories/LoadBalancerRepository.cs", "CountActiveByTenantAsync"),
        ("Titan.Infrastructure/Repositories/VmSnapshotRepository.cs", "ListBillableAsync"),
        ("Titan.Infrastructure/Repositories/VolumeSnapshotRepository.cs", "ListBillableAsync"),
        ("Titan.Tests/Fakes/FakeLoadBalancerRepository.cs", "CountActiveByTenantAsync"),
    ];

    private static readonly string[] ScannedDirs = ["Titan.Infrastructure/Repositories", "Titan.Tests/Fakes"];

    private static readonly Regex QueryName =
        new(@"public\s+(?:async\s+)?Task<[^\n]+?>\s+(CountActiveByTenantAsync|CountPublicIpv6ActiveByTenantAsync|ListBillableAsync)\s*\(");

    // The members a rule reads; a query body that names one writes the rule itself.
    private static readonly Regex StateMember = new(@"\b(CurrentExistence|DesiredExistence|CurrentState|PendingRequestId|Pending)\b");

    private static readonly Regex DomainRule = new(@"\.(OccupiesQuota|OccupiesPublicIpv6Quota|IsBilled)\b");

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"(?<!:)//[^\n]*", " ");

    // An expression-bodied query ends at its first semicolon; the lambdas inside have none.
    private static List<(string File, string Method, string Body)> FoundQueries()
    {
        string root = SolutionRoot();
        var found = new List<(string, string, string)>();
        foreach (string dir in ScannedDirs)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                string text = StripComments(File.ReadAllText(path));
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                foreach (Match match in QueryName.Matches(text))
                {
                    int end = text.IndexOf(';', match.Index);
                    found.Add((relative, match.Groups[1].Value, text[match.Index..end]));
                }
            }
        }
        return found;
    }

    [Fact]
    public void TheScan_FindsEveryQueryOnTheList_AndNoOther()
    {
        var found = FoundQueries().Select(q => (q.File, q.Method)).OrderBy(q => q.File).ThenBy(q => q.Method).ToList();

        Assert.Equal(RuleQueries.OrderBy(q => q.File).ThenBy(q => q.Method).ToList(), found);
    }

    [Fact]
    public void EveryQuota_AndBillingQuery_AppliesTheDomainRule_AndWritesNoStateCondition()
    {
        var queries = FoundQueries();
        Assert.Equal(RuleQueries.Length, queries.Count);

        var writesState = queries.Where(q => StateMember.IsMatch(q.Body)).Select(q => $"{q.File} {q.Method}").ToList();
        var withoutRule = queries.Where(q => !DomainRule.IsMatch(q.Body)).Select(q => $"{q.File} {q.Method}").ToList();

        Assert.True(writesState.Count == 0, "Writes the state condition instead of the Domain rule: " + string.Join(", ", writesState));
        Assert.True(withoutRule.Count == 0, "Applies no Domain rule: " + string.Join(", ", withoutRule));
    }
}
