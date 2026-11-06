using System.IO;
using System.Text.RegularExpressions;
using Titan.Application.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation: the state machine belongs in the aggregate.
// Four invariants, all read from the source tree, all failing in BOTH directions like the
// titan-ops architecture/code-structure.md#module-boundary allowlist: an undeclared violation is red, and a
// declaration for a violation that no longer exists is also red.
//
// What these tests are SILENT about: they read source text, not behaviour. A rule expressed some
// other way — a method parameter, a static helper, a switch in an endpoint — is invisible here, the
// same blind spot ModuleBoundaryTests carries. Green does not mean "every transition law is in a
// domain aggregate"; it means "no law is in the shapes we know how to look for".
public class StateTransitionOwnershipTests
{
    // Codes that mean "you asked for something this thing's current state does not allow" — a
    // business rule about a transition. NOT_FOUND / QUOTA / NAME_TAKEN are different rules and are
    // deliberately out of scope. ALREADY_PAID is in the list because it is exactly that rule wearing
    // a different name: Invoice.MarkPaid refuses a request made from a terminal state.
    private static readonly Regex TransitionThrow = new(
        @"throw new (Business|BadRequest|Conflict)Exception\(""(?<code>[A-Z_]*(INVALID_STATUS|INVALID_STATE|NOT_ACTIVE|NOT_READY|ALREADY_PAID))""",
        RegexOptions.Compiled);

    // Key: "<file>|<code>|<distinctive message fragment>". Line numbers are NOT part of the key —
    // they move for unrelated reasons and would make this allowlist rot on every edit.
    // Every row must say WHY, and a row whose reason is "not done yet" must name the story.
    private static readonly Dictionary<string, string> ApplicationAllowlist = new()
    {
        // --- Payload / parse validation: NOT transition legality. These reject a malformed request
        // before any aggregate is consulted ("what is 'Frobnicated'?"), which is the adapter layer's
        // job. They are BadRequestException (400), not BusinessException (422).
        ["EndTemplateSyncRequestCommandHandler.cs|INVALID_STATUS|Unknown request outcome"] = "parse: unknown outcome word from the worker's template sync report",
        ["UpdateTenantStatusCommandHandler.cs|INVALID_STATUS|Invalid status"] = "parse: unknown enum value from the admin request",
        // PendingApproval parses as TenantStatus but is not an admin target.
        ["UpdateTenantStatusCommandHandler.cs|INVALID_STATUS|A tenant can only be set to"] = "parse: only Active and Suspended are admin targets",
        ["VmReports.cs|INVALID_STATUS|Unknown VM status"] = "parse: unknown enum value from the worker callback, shared by every VM callback handler",
        ["VmReports.cs|INVALID_STATUS|Unknown request outcome"] = "parse: unknown outcome word from the worker's VM or VM snapshot request report",
        ["ObserveVmCommandHandler.cs|INVALID_STATUS|Observed is required when the instance exists"] = "payload: an observation of an existing instance that does not say its status",
        ["VolumeReports.cs|INVALID_STATUS|Unknown request outcome"] = "parse: unknown outcome word from the worker's volume or volume snapshot request report",
        ["NetworkReports.cs|INVALID_STATUS|Unknown request outcome"] = "parse: unknown outcome word from the worker's network or firewall rule request report",
        ["NetworkReports.cs|INVALID_STATUS|Unknown network status"] = "parse: unknown enum value from the worker callback, shared by every network callback handler",
        ["NetworkReports.cs|INVALID_STATUS|Unknown firewall rule state"] = "parse: unknown enum value from the worker callback, shared by every firewall rule callback handler",
        ["LoadBalancerReports.cs|INVALID_STATUS|Unknown request outcome"] = "parse: unknown outcome word from the worker's load balancer request report",
        ["CreateLoadBalancerCommandHandler.cs|LOAD_BALANCER_INVALID_STATE|This network has no free load balancer address left"] = "capacity: every VIP number of the network is taken, a count over many aggregates, not one's state",
    };

    // Mark* is the OBSERVATION vocabulary (titan-ops architecture/intent-and-observation.md#observation-converges): a report
    // of what the provider is, which cannot be illegal. So a Mark* method must never throw a transition-legality
    // error; each exception below says why it does.
    private static readonly Dictionary<string, string> MarkThrowAllowlist = new()
    {
        ["Vm.MarkObserved"] = "refuses a status that is not an Incus word: the aggregate keeps ObservedState to those five. " +
                              "ObserveVmCommandHandler parses the report with Vm.IsIncusStatus before it loads the VM, so no " +
                              "reconciler callback reaches this throw",
        ["Invoice.MarkPaid"] = "intent (an admin presses Mark Paid) named as observation — it DOES " +
                               "validate internally, which is what Tôn Chủ's rule asks; only the name " +
                               "is wrong. Rename to SettleOutsideTitan() when Billing gets a story",
    };

    // Aggregates with a Status but no IsDeleted or IsTerminal, i.e. whose terminal state is not absorbing.
    private static readonly Dictionary<string, string> NoTerminalGuardAllowlist = new()
    {
        ["Tenant/Tenant"] = "no terminal state exists: a tenant is never deleted, only suspended, " +
            "and Suspend()/Activate()/Approve() each validate internally",
        ["Invoice/Invoice"] = "no story yet (Billing): Paid/Settled are terminal and guarded by the intent methods themselves",
        ["TopUp/TopUp"] = "no story yet (Billing): Confirmed/Failed are terminal and guarded by Confirm()/Fail()",
        // A promo TopUp is BORN Confirmed via GrantPromo() — a factory, not a transition — so it adds no
        // status mutation to guard here; Confirm()/Fail() still refuse it (already terminal).
        ["ProviderCommand/ProviderCommand"] = "Failed is not absorbing on purpose: a repeat of the key reopens a " +
            "Failed row (titan-ops architecture/resource-lifecycle.md#one-key-per-request)",
        ["BackupRecord/BackupRecord"] = "a row of the cache the backup refresh keeps from the bucket, not a state machine: Complete or " +
            "Incomplete only reports what barman wrote, and the next refresh rewrites it from the bucket",
        ["PaymentGatewayTransaction/PaymentGatewayTransaction"] = "no terminal risk: Status is set in Create() and never "
            + "mutated again — there is no observation path into this aggregate",
    };

    // ── a caller may not assert a provider id the aggregate owns (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id) ──
    //
    // `x.ExternalId!` is a claim the application layer cannot prove — that a resource on the provider carries an id is
    // an invariant of the aggregate, so a Domain method that throws without it hands it out (Volume.Attach,
    // Network.ProviderIdForVm, …). The VM's rows in the allowlist are the named exception.
    //
    // Only `req.X!` passes: worker callback payload, already checked by a FastEndpoints validator, owned by no
    // aggregate. Comments are stripped before matching, so prose that names the pattern does not trip the guard.
    private static readonly Regex ProviderIdAssertion =
        new(@"(?<recv>\w+)\.(?<prop>ExternalTemplateId|ExternalId)!", RegexOptions.Compiled);

    // The VM hands out no id (titan-ops features/vm/design.md#vm-status, Tôn Chủ 2026-11-05): the intent the handler
    // called just before refused every VM without one, so its status proves the id is there.
    private const string VmIdFollowsTheIntent = "the VM intent called just before refuses a VM without a provider id";


    private static readonly Dictionary<string, string> ProviderIdAssertionAllowlist = new()
    {
        ["StopVmCommandHandler.cs|vm.ExternalId"] = VmIdFollowsTheIntent,
        ["StartVmCommandHandler.cs|vm.ExternalId"] = VmIdFollowsTheIntent,
        ["DeleteVmCommandHandler.cs|vm.ExternalId"] = VmIdFollowsTheIntent + " (Deleting; Deleted returned before)",
        ["DeleteAdminVmCommandHandler.cs|vm.ExternalId"] = VmIdFollowsTheIntent + " (Deleting; Deleted returned before)",
        ["CreateVmSnapshotCommandHandler.cs|vm.ExternalId"] = VmIdFollowsTheIntent + " (AddSnapshot)",
        ["GetVmShellTargetQueryHandler.cs|vm.ExternalId"] = "EnsureShellCanOpen passes only Running or Ready, which only a report carrying the id reaches",
    };

    [Fact]
    public void NoCallerAssertsAnAggregateProviderId()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var (file, src) in ReadSources(Path.Combine(FindRepoDir(), "Titan.Application")))
        {
            scanned++;
            var code = Regex.Replace(src, @"//.*$", "", RegexOptions.Multiline);
            foreach (var m in ProviderIdAssertion.Matches(code).Cast<Match>())
            {
                var recv = m.Groups["recv"].Value;
                var key = $"{Path.GetFileName(file)}|{recv}.{m.Groups["prop"].Value}";
                if (recv == "req" && !ProviderIdAssertionAllowlist.ContainsKey(key)) continue;
                if (ProviderIdAssertionAllowlist.ContainsKey(key)) continue;
                offenders.Add(key);
            }
        }

        // 0 offenders out of 0 files reads identical to 0 out of 51, and only one is a boundary held.
        Assert.True(scanned >= 40, $"Only {scanned} Application source files scanned — the guard is blind.");

        Assert.True(offenders.Count == 0,
            "The application layer asserts a provider id it cannot prove. Have the aggregate RETURN "
            + "the id from the method that validates the state (F-11), or add an allowlist row saying "
            + "why it cannot:\n  " + string.Join("\n  ", offenders.Distinct().OrderBy(x => x, StringComparer.Ordinal)));
    }

    [Fact] // other direction — a row for an assertion already removed is also a failure
    public void ProviderIdAssertionAllowlist_HasNoStaleRows()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, src) in ReadSources(Path.Combine(FindRepoDir(), "Titan.Application")))
        {
            var code = Regex.Replace(src, @"//.*$", "", RegexOptions.Multiline);
            foreach (var m in ProviderIdAssertion.Matches(code).Cast<Match>())
                live.Add($"{Path.GetFileName(file)}|{m.Groups["recv"].Value}.{m.Groups["prop"].Value}");
        }

        var stale = ProviderIdAssertionAllowlist.Keys.Where(k => !live.Contains(k))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "Allowlist rows for provider-id assertions that no longer exist. Delete them:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void NoUndeclaredTransitionRule_LivesInApplication()
    {
        var found = ScanApplicationThrows();
        var undeclared = found.Keys.Where(k => !ApplicationAllowlist.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(undeclared.Count == 0,
            "Transition-legality rules found in Titan.Application with no allowlist row. Move each onto "
            + "the aggregate that owns the state (titan-ops architecture/intent-and-observation.md#intent-versus-observation), or add a row saying why it cannot move:\n  "
            + string.Join("\n  ", undeclared.Select(k => $"{k}  ({found[k]})")));
    }

    [Fact] // the other direction: a row for a rule that has already moved is also a failure
    public void ApplicationAllowlist_HasNoStaleRows()
    {
        var found = ScanApplicationThrows();
        var stale = ApplicationAllowlist.Keys.Where(k => !found.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(stale.Count == 0,
            "Allowlist rows describing rules that are no longer in Titan.Application. Delete them — a "
            + "stale row is how an allowlist stops being a measurement:\n  " + string.Join("\n  ", stale));
    }

    [Fact] // reads each Mark* body, not just the name
    public void MarkMethods_NeverThrowTransitionErrors()
    {
        var offenders = new List<string>();
        foreach (var (file, src) in ReadSources(Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates")))
        {
            var aggregate = Path.GetFileNameWithoutExtension(file);
            // `?` is in the return-type class: Volume.MarkObserved returns VolumeTransition?, and a pattern without it
            // would skip that method silently — the scan measuring less, still green. `internal` for the same
            // reason: a child's mutations (VmTemplateCluster) are internal, reached only through its root.
            foreach (var m in Regex.Matches(src, @"(?:public|internal) (?:void|[A-Za-z<>?]+) (Mark\w*)\([^)]*\)\s*\{").Cast<Match>())
            {
                var end = src.IndexOf("\n    }", m.Index, StringComparison.Ordinal);
                var body = end > 0 ? src[m.Index..end] : src[m.Index..];
                if (TransitionThrow.IsMatch(body)) offenders.Add($"{aggregate}.{m.Groups[1].Value}");
            }
        }

        var undeclared = offenders.Where(o => !MarkThrowAllowlist.ContainsKey(o)).OrderBy(o => o).ToList();
        Assert.True(undeclared.Count == 0,
            "Mark* is the observation vocabulary (titan-ops architecture/intent-and-observation.md#observation-converges) and must not refuse a transition. A throw "
            + "here 500s the internal callback, which the reconciler logs and skips forever "
            + "— the VM freezes. Split intent out into an Ensure*() guard of the request:\n  "
            + string.Join("\n  ", undeclared));

        var stale = MarkThrowAllowlist.Keys.Where(k => !offenders.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(stale.Count == 0, "MarkThrowAllowlist rows for methods that no longer throw:\n  " + string.Join("\n  ", stale));
    }

    // Measures the mechanism, not the existence of a test: an aggregate that declares IsDeleted (WalletMutation: IsTerminal) must consult
    // it in EVERY Mark* method, because the terminal absorb (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs)
    // is enforced by that early return and nothing else.
    [Fact]
    public void EveryAggregateWithAnAbsorbingState_ChecksItInEveryMarkMethod()
    {
        var missing = new List<string>();
        var declared = new List<string>();

        foreach (var (file, src) in ReadSources(Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates")))
        {
            var key = $"{Directory.GetParent(file)!.Name}/{Path.GetFileNameWithoutExtension(file)}";
            if (!src.Contains("public bool IsDeleted") && !src.Contains("public bool IsTerminal")) continue;
            declared.Add(key);

            foreach (var m in Regex.Matches(src, @"(?:public|internal) (?:void|[A-Za-z<>?]+) (Mark\w*)\([^)]*\)\s*\{").Cast<Match>())
            {
                var name = m.Groups[1].Value;
                var end = src.IndexOf("\n    }", m.Index, StringComparison.Ordinal);
                var body = end > 0 ? src[m.Index..end] : src[m.Index..];
                // The method that ENTERS the terminal state is the one exception, by definition. CurrentState too:
                // a split aggregate has no Status, and a pattern without it would not recognise its entering method.
                // CurrentExistence too: a split aggregate enters its terminal state on the existence axis.
                if (body.Contains("IsDeleted") || body.Contains("IsTerminal") || Regex.IsMatch(body, @"(?:Status|CurrentState|CurrentExistence) = \w+(?:Status|State|Existence)\.(Destroyed|Deleted)")) continue;
                missing.Add($"{key}.{name}");
            }
        }

        Assert.NotEmpty(declared); // the scan itself must have found something, or it proves nothing
        Assert.True(missing.Count == 0,
            "These Mark* methods can move an aggregate OUT of its terminal state (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs). For a VM "
            + "that means a destroyed machine reported Running gets billed (ComputeMeterService.cs:35). "
            + "Add `if (IsDeleted) return;` as the first statement:\n  " + string.Join("\n  ", missing));

        // And the aggregates with a Status but no IsDeleted at all must be declared, with a reason.
        var withStatus = ReadSources(Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates"))
            // CurrentState: an aggregate split into desired/current (titan-ops architecture/desired-and-current.md#desired-and-current)
            // still has a state that must absorb; dropping it here would shrink the scan without a signal.
            .Where(t => Regex.IsMatch(t.Source, @"public (?:\w+Status|string) Status \{ get; private set; \}")
                     || Regex.IsMatch(t.Source, @"public \w+State\?? CurrentState \{ get; private set; \}"))
            .Select(t => $"{Directory.GetParent(t.File)!.Name}/{Path.GetFileNameWithoutExtension(t.File)}")
            .ToList();

        var unguarded = withStatus.Except(declared).Where(k => !NoTerminalGuardAllowlist.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(unguarded.Count == 0,
            "Aggregates with a Status and no IsDeleted or IsTerminal. Add the guard, or an allowlist row naming the story:\n  "
            + string.Join("\n  ", unguarded));

        var staleTerminal = NoTerminalGuardAllowlist.Keys.Where(k => declared.Contains(k) || !withStatus.Contains(k))
            .OrderBy(k => k).ToList();
        Assert.True(staleTerminal.Count == 0,
            "NoTerminalGuardAllowlist rows that are no longer true (the aggregate now guards, or is gone):\n  "
            + string.Join("\n  ", staleTerminal));
    }

    // EVERY mutation inside an aggregate validates internally (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation):
    // a guard the caller has to remember is advice; this test is what makes it an invariant.
    //
    // Internal validation counts in three shapes: it throws; it consults IsDeleted (absorb instead
    // of refuse — the observation path, titan-ops architecture/intent-and-observation.md#observation-converges); or it delegates to an Ensure*() on itself
    // (EnsureSnapshottable, EnsureNoCreateWaiting, …), which a text scan looking only for `throw` would report as a violation.
    private const string TemplateReport = "the report of a sync's own command, ignored unless it names the waiting " +
        "request: that id check is the guard. No terminal state to absorb (a template is never Deleted), and no " +
        "outcome is illegal from a waiting sync (titan-ops architecture/resource-lifecycle.md#shape).";

    private const string PartOfTheReport = "private helper, reached only from the type's FinishOperation and " +
        "FailOperation, which ignore a report that names another operation, and absorb one for a Deleted resource where " +
        "the type can be Deleted, before calling it; it records what the worker saw, which a waiting operation may always take. A check here would " +
        "repeat theirs.";

    private static readonly Dictionary<string, string> NoInvariantAllowlist = new()
    {
        ["VmTemplateCluster.MarkNotFound"] = "there is genuinely no invariant to enforce: NotFound is reachable " +
            "from every existence, carries no required field, and has no terminal state to absorb. " +
            "Inventing a check here would be a check that cannot fail — which is worse than none, " +
            "because it reads like protection. Declared, not enforced.",
        ["VmTemplateCluster.FinishOperation"] = TemplateReport,
        ["VmSnapshot.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["Volume.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["VolumeSnapshot.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["Network.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["FirewallRule.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["LoadBalancer.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["VmTemplateCluster.RecordWhatTheWorkerSaw"] = PartOfTheReport,
        ["ProviderCommand.Reopen"] = "no state refuses a repeat of a key: an open or Done row absorbs it " +
            "(returns false); a Failed row runs again. A throw would turn a resent request into an error every " +
            "handler must recognise first.",
    };

    [Fact]
    public void EveryStatusMutation_ValidatesInternally()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var (file, src) in ReadSources(Path.Combine(FindRepoDir(), "Titan.Domain", "Aggregates")))
        {
            var type = Path.GetFileNameWithoutExtension(file);
            foreach (var m in Regex.Matches(src,
                @"(?:public|private|internal) (?:void|[A-Za-z<>?]+) ([A-Z]\w*)\([^)]*\)\s*(?:=>|\{)").Cast<Match>())
            {
                var end = src.IndexOf("\n    }", m.Index, StringComparison.Ordinal);
                var body = end > 0 ? src[m.Index..end] : src[m.Index..];
                // DesiredState/CurrentState too: without them every split aggregate's mutation would drop out
                // of this scan while the count stayed above 20 — green and blind.
                // The existence pair too (titan-ops architecture/desired-and-current.md#existence-axis).
                if (!Regex.IsMatch(body, @"(?:Status|CurrentState|DesiredState|CurrentExistence|DesiredExistence)\s*=(?!=)")) continue;
                if (body.Contains("static ")) continue;              // Create() sets initial state
                scanned++;

                // A report move of the VM refuses every other source status by returning false
                // (`if (Status != VmStatus.Stopping || …) return false;`, titan-ops features/vm/design.md#vm-status).
                var validates = body.Contains("throw")
                             || body.Contains("IsDeleted")
                             || body.Contains("IsTerminal")
                             || Regex.IsMatch(body, @"\bEnsure[A-Z]\w*\(\)")
                             || Regex.IsMatch(body, @"\bStatus != VmStatus\.\w+ \|\| PendingOperationId != operationId\)\s*\{\s*return false;")
                             // A snapshot's report moves only a VM still Snapshotting: the status check is the guard.
                             || Regex.IsMatch(body, @"if \(Status == VmStatus\.Snapshotting && ");
                if (!validates) offenders.Add($"{type}.{m.Groups[1].Value}");
            }
        }

        Assert.True(scanned > 20, $"scan found only {scanned} status mutations — it is measuring less than it claims");

        var undeclared = offenders.Where(o => !NoInvariantAllowlist.ContainsKey(o)).OrderBy(o => o).ToList();
        Assert.True(undeclared.Count == 0,
            "These mutations change Status without validating anything themselves, so they are only safe "
            + "while every caller remembers to call a guard first. Fold the check in (throw for intent, "
            + "`if (IsDeleted) return;` for observation), or add an allowlist row saying why no "
            + "invariant exists:\n  " + string.Join("\n  ", undeclared));

        var stale = NoInvariantAllowlist.Keys.Where(k => !offenders.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(stale.Count == 0, "NoInvariantAllowlist rows that now validate:\n  " + string.Join("\n  ", stale));
    }

    // ---------- helpers ----------

    private static Dictionary<string, string> ScanApplicationThrows()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = Path.Combine(FindRepoDir(), "Titan.Application");
        foreach (var (file, src) in ReadSources(root))
        {
            foreach (var m in TransitionThrow.Matches(src).Cast<Match>())
            {
                // Take the message fragment that follows the code, trimmed to something stable.
                var after = src[(m.Index + m.Length)..];
                var msg = Regex.Match(after, @"""([^""]{8,60})").Groups[1].Value;
                var frag = Fragment(msg);
                found[$"{Path.GetFileName(file)}|{m.Groups["code"].Value}|{frag}"] = m.Groups["code"].Value;
            }
        }
        Assert.NotEmpty(found); // a scan that finds nothing would make both directions vacuously green
        return found;
    }

    // The allowlist key stores a human-chosen fragment, so match on containment in both directions.
    private static string Fragment(string message)
    {
        foreach (var key in ApplicationAllowlist.Keys)
        {
            var wanted = key.Split('|')[2];
            if (message.Contains(wanted, StringComparison.Ordinal)) return wanted;
        }
        return message.Trim();
    }

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
