using System.IO;
using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

// titan-ops architecture/desired-and-current.md#desired-and-current: every resource the
// control plane provisions to the provider keeps two states, except the VM, the network, the firewall rule and the VM snapshot, which keep one status (OneStatus below). A person's request writes DesiredState; only an
// observation of the provider (Mark*, reached from the worker's callback), or a request's own report
// (FinishOperation, FailOperation), writes CurrentState, apart from the allowlist below. The two
// directions below are what makes "currentState is updated in the reconcile direction" a build failure
// instead of a convention.
//
// What these tests are SILENT about: they read source text. An assignment made some other way — reflection,
// a helper that takes the aggregate as a parameter, EF writing a shadow property — is invisible here. Green
// means "no assignment in the shapes we look for", not "no path exists".
public class DesiredCurrentOwnershipTests
{
    // The provisioned aggregates that keep one status instead of the split, by name: the VM
    // (titan-ops features/vm/design.md#vm-status), the network and the firewall rule (titan-ops
    // features/network/design.md#network-lifecycle), and the VM snapshot (titan-ops features/vm/design.md#vm-snapshot).
    // Their status moves only through their own table of moves, so the scans below leave them out, and the last fact
    // checks they really have no split.
    private static readonly string[] OneStatus = ["Vm", "Network", "FirewallRule", "VmSnapshot"];

    // Intent methods that write CurrentState on purpose. Each is a resource that never reached the provider:
    // there is no observation coming, so the control plane is the only witness
    // (titan-ops architecture/desired-and-current.md#delete-locally).
    private static readonly Dictionary<string, string> IntentWritesCurrentAllowlist = new()
    {
        ["Volume.Delete"] = "the delete of a volume whose create failed before Incus had it happens in the request: nothing to observe",
        ["VolumeSnapshot.Delete"] = "the delete of a snapshot whose create failed before Incus had it happens in the request: nothing to observe",
        ["LoadBalancer.Delete"] = "the delete of a load balancer whose create failed before Incus had it happens in the request: nothing to observe",
        ["VolumeSnapshot.EndWithItsVolume"] = "part of Volume.FinishOperation: the volume's delete Done takes its snapshots with it (titan-ops architecture/resource-lifecycle.md#shape)",
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
            if (method.IsObservation || method.IsReport)
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

    // On a type with two states, the report of a request's own command (FinishOperation for a Done, FailOperation for a
    // give-up, titan-ops architecture/resource-lifecycle.md#shape) is the one pair of methods that may write both: what the
    // worker saw is current, and a failed request puts desired back. They are neither intents nor observations, so both
    // scans skip them and the private RecordWhatTheWorkerSaw they share; a root reaches a child's through
    // Finish<Child>Operation and Fail<Child>Operation. The types in OneStatus end an operation with one Succeeded and one
    // Failed move per kind instead, and this scan does not read them.
    [Fact]
    public void ReportMethodsOfATypeWithTwoStates_AreOnlyFinishAndFailOperation()
    {
        var reports = SplitAggregateMethods().Where(m => m.IsReport).Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        string[] expected =
        [
            "LoadBalancer.FailOperation", "LoadBalancer.FinishOperation", "LoadBalancer.RecordWhatTheWorkerSaw",
            "VmTemplateCluster.FailOperation", "VmTemplateCluster.FinishOperation", "VmTemplateCluster.RecordWhatTheWorkerSaw",
            "Volume.FailOperation", "Volume.FailSnapshotOperation", "Volume.FinishOperation", "Volume.FinishSnapshotOperation",
            "Volume.RecordWhatTheWorkerSaw",
            "VolumeSnapshot.FailOperation", "VolumeSnapshot.FinishOperation", "VolumeSnapshot.RecordWhatTheWorkerSaw",
        ];
        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), reports);
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
            bool isTheAggregateRoot = Regex.IsMatch(src, $@"public (?:partial )?class {type}\b");
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

        // No allowlist beyond OneStatus: a new provisioned aggregate is born split, or this fails.
        Assert.Empty(OneStatus.Intersect(split));
        var missing = provisioned.Except(split).Except(OneStatus).OrderBy(t => t).ToList();
        Assert.True(missing.Count == 0,
            "Provisioned aggregates without DesiredState + CurrentState:\n  " + string.Join("\n  ", missing));
    }

    // ---------- helpers ----------

    private sealed record AggregateMethod(string Key, bool IsObservation, string Body)
    {
        // Finish<Child>Operation and Fail<Child>Operation: a root's way in to its child's reports
        // (titan-ops architecture/code-structure.md#child-entity). RecordWhatTheWorkerSaw is the part they share.
        public bool IsReport => Regex.IsMatch(Key, @"\.(?:(?:Finish|Fail)\w*Operation|RecordWhatTheWorkerSaw)$");
    }

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

    // `?` in the type: a nullable current state — a pattern without it would drop the type from every scan here, still
    // green. The VM, the network, the firewall rule and the VM snapshot keep one status and are out on purpose (OneStatus). Desired is
    // DesiredState or, where Titan sets no Incus status, only DesiredExistence; current is CurrentState or, where Incus
    // has no status word (volume, volume snapshot), only CurrentExistence (titan-ops architecture/desired-and-current.md#existence-axis) — a
    // pattern for one name alone would drop the others the same way.
    private static bool DeclaresBothStates(string src) =>
        Regex.IsMatch(src, @"public [\w?]+ Desired(?:State|Existence) \{ get; private set; \}")
        && Regex.IsMatch(src, @"public [\w?]+ Current(?:State|Existence) \{ get; private set; \}");

    private static string AggregatesDir() => Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates");

    private static List<(string File, string Source)> ReadSources(string root) =>
        PartialTypeSources.Merge(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)))
            .ToList());

    private static string FindRepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
