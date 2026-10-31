using System.IO;
using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

// titan-ops architecture/desired-and-current.md#desired-and-current: every resource the
// control plane provisions to the provider keeps two states. A person's request writes DesiredState; only an
// observation of the provider (Mark*, reached from the worker's callback) writes CurrentState. The two
// directions below are what makes "currentState is updated in the reconcile direction" a build failure
// instead of a convention.
//
// What these tests are SILENT about: they read source text. An assignment made some other way — reflection,
// a helper that takes the aggregate as a parameter, EF writing a shadow property, a method not named Mark* that a Mark* calls to
// write desired (Network.MarkRequestFailed reaches AbandonRequest: a give-up withdraws the delete,
// titan-ops architecture/desired-and-current.md#abandoned-request) — is invisible here. Green
// means "no assignment in the shapes we look for", not "no path exists".
public class DesiredCurrentOwnershipTests
{
    // Intent methods that write CurrentState on purpose. Each is a resource that never reached the provider:
    // there is no observation coming, so the control plane is the only witness
    // (titan-ops architecture/desired-and-current.md#delete-locally).
    private static readonly Dictionary<string, string> IntentWritesCurrentAllowlist = new()
    {
        ["Vm.DeleteLocally"] = "delete of a VM with no provider id: nothing to terminate, no reconciler to report it",
        ["Network.DeleteLocally"] = "delete of a network whose create failed before Incus had it: nothing to observe",
        ["FirewallRule.DeleteLocally"] = "delete of a rule whose create failed (no provider id): nothing to observe",
        ["Volume.DeleteLocally"] = "delete of a volume whose create failed before Incus had it: nothing to observe",
        ["VolumeSnapshot.DeleteLocally"] = "delete of a snapshot whose create failed before Incus had it: nothing to observe",
        ["VmSnapshot.DeleteLocally"] = "delete of a snapshot whose create failed before Incus had it: nothing to observe",
        ["LoadBalancer.DeleteLocally"] = "delete of a load balancer whose create failed before Incus had it: nothing to observe",
    };

    private static readonly Regex MethodHead = new(
        @"(?<mods>(?:public|private|internal)(?: static)?) (?:void|[A-Za-z<>?.]+) (?<name>[A-Z]\w*)\([^)]*\)\s*(?<shape>=>|\{)",
        RegexOptions.Compiled);

    // The existence pair, and a volume's attachment pair, have the same two writers as the state pair
    // (titan-ops architecture/desired-and-current.md#existence-axis, titan-ops features/volume/design.md#volume-state-is-existence).
    private static readonly Regex WritesCurrent = new(@"\bCurrent(?:State|Existence|Attached\w*)\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex WritesDesired = new(@"\bDesired(?:State|Existence|Attached\w*)\s*=(?!=)", RegexOptions.Compiled);

    [Fact]
    public void IntentMethods_DoNotWriteCurrentState()
    {
        var offenders = new List<string>();
        foreach (var method in SplitAggregateMethods())
        {
            if (method.IsObservation)
            {
                continue;
            }
            if (WritesCurrent.IsMatch(method.Body))
            {
                offenders.Add(method.Key);
            }
        }

        var undeclared = offenders.Where(o => !IntentWritesCurrentAllowlist.ContainsKey(o)).OrderBy(o => o).ToList();
        Assert.True(undeclared.Count == 0,
            "A request writes CurrentState. CurrentState is only what the provider was observed to be — write "
            + "DesiredState and let the reconciler report the result:\n  " + string.Join("\n  ", undeclared));

        var stale = IntentWritesCurrentAllowlist.Keys.Where(k => !offenders.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(stale.Count == 0, "IntentWritesCurrentAllowlist rows that no longer write CurrentState:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void ObservationMethods_DoNotWriteDesiredState()
    {
        var offenders = SplitAggregateMethods()
            .Where(m => m.IsObservation && WritesDesired.IsMatch(m.Body))
            .Select(m => m.Key)
            .OrderBy(k => k)
            .ToList();

        Assert.True(offenders.Count == 0,
            "An observation writes DesiredState. What a person asked for is theirs; an observation may only "
            + "report what the provider is:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EveryProvisionedAggregate_SplitsDesiredAndCurrent()
    {
        var provisioned = new List<string>();
        var split = new List<string>();
        foreach (var (file, src) in ReadSources(AggregatesDir()))
        {
            var type = Path.GetFileNameWithoutExtension(file);
            bool isTheAggregateRoot = Regex.IsMatch(src, $@"public class {type}\b");
            bool carriesAProviderId = Regex.IsMatch(src, @"public string\? External\w*Id \{");
            if (!isTheAggregateRoot || !carriesAProviderId)
            {
                continue;
            }
            provisioned.Add(type);
            if (DeclaresBothStates(src))
            {
                split.Add(type);
            }
        }

        // 8 = the resources the control plane provisions to the provider (the tenant has no provider account:
        // titan-ops architecture/tenancy-and-admin.md#approval-is-activation). A scan that finds fewer is blind, not green.
        Assert.Equal(8, provisioned.Count);

        // No allowlist: a new provisioned aggregate is born split, or this fails.
        var missing = provisioned.Except(split).OrderBy(t => t).ToList();
        Assert.True(missing.Count == 0,
            "Provisioned aggregates without DesiredState + CurrentState:\n  " + string.Join("\n  ", missing));
    }

    // ---------- helpers ----------

    private sealed record AggregateMethod(string Key, bool IsObservation, string Body);

    private static List<AggregateMethod> SplitAggregateMethods()
    {
        var methods = new List<AggregateMethod>();
        var aggregates = 0;
        foreach (var (file, src) in ReadSources(AggregatesDir()))
        {
            if (!DeclaresBothStates(src))
            {
                continue;
            }
            aggregates++;
            var type = Path.GetFileNameWithoutExtension(file);
            foreach (var m in MethodHead.Matches(src).Cast<Match>())
            {
                bool isFactory = m.Groups["mods"].Value.Contains("static");
                if (isFactory)
                {
                    continue; // a factory sets the initial value of both — nobody has asked or observed yet
                }

                string body;
                if (m.Groups["shape"].Value == "=>")
                {
                    int semicolon = src.IndexOf(';', m.Index);
                    body = src[m.Index..(semicolon + 1)];
                }
                else
                {
                    int end = src.IndexOf("\n    }", m.Index, StringComparison.Ordinal);
                    body = end > 0 ? src[m.Index..end] : src[m.Index..];
                }

                string name = m.Groups["name"].Value;
                methods.Add(new AggregateMethod($"{type}.{name}", name.StartsWith("Mark", StringComparison.Ordinal), body));
            }
        }

        // Zero split aggregates makes both directions vacuously green.
        Assert.True(aggregates >= 2, $"Only {aggregates} aggregates declare DesiredState + CurrentState — the scan is blind.");
        return methods;
    }

    // `?` in the type: Vm.CurrentState is VmState? (null while Incus has no instance) — a pattern without it would
    // drop the VM from every scan here, still green. Desired is DesiredState or, where Titan sets no Incus status
    // (network, firewall rule), only DesiredExistence; current is CurrentState or, where Incus has no status word
    // (volume, snapshots), only CurrentExistence (titan-ops architecture/desired-and-current.md#existence-axis) — a
    // pattern for one name alone would drop the others the same way.
    private static bool DeclaresBothStates(string src) =>
        Regex.IsMatch(src, @"public [\w?]+ Desired(?:State|Existence) \{ get; private set; \}")
        && Regex.IsMatch(src, @"public [\w?]+ Current(?:State|Existence) \{ get; private set; \}");

    private static string AggregatesDir() => Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates");

    private static List<(string File, string Source)> ReadSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)))
            .ToList();

    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
