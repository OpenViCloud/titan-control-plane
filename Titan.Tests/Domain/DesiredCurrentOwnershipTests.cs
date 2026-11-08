using System.IO;
using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

// titan-ops architecture/desired-and-current.md#desired-and-current: every resource the control plane provisions to the
// provider keeps one status and an Operation of its own, and no Domain type keeps a desired existence, a current existence
// or a desired and a current state beside it. That holds for Vm, Network, FirewallRule, VmSnapshot, Volume, VolumeSnapshot,
// VmTemplateCluster and LoadBalancer (OneStatus below). Their status moves only
// through their own table of moves, which StateTransitionOwnershipTests and RequestLifecycleShapeTests read; this file
// keeps the other two ways back to the old shape closed.
//
// What these tests are SILENT about: they read source text. A second status kept under another name, or a state pair
// declared in a shape the pattern does not match, is invisible here. Green means "no property of the old names", not "no
// second status exists".
public class DesiredCurrentOwnershipTests
{
    // The provisioned aggregates, by name: the VM (titan-ops features/vm/design.md#vm-status), the network and the firewall
    // rule (titan-ops features/network/design.md#network-lifecycle), the VM snapshot (titan-ops features/vm/design.md#vm-snapshot),
    // the volume and its snapshot (titan-ops features/volume/design.md#volume-state-is-existence), the template in a cluster
    // (titan-ops features/catalog/design.md#template-state-is-existence) and the load balancer
    // (titan-ops features/load-balancer/design.md#lb-lifecycle).
    private static readonly string[] OneStatus =
        ["Vm", "Network", "FirewallRule", "VmSnapshot", "Volume", "VolumeSnapshot", "VmTemplateCluster", "LoadBalancer"];

    // The old axes: an existence pair, or a desired and a current state. `?` in the type: a nullable one would otherwise
    // slip past the pattern.
    private static readonly Regex AnAxis = new(
        @"public [\w?<>]+ (?:Desired|Current)(?:State|Existence) \{", RegexOptions.Compiled);

    [Fact]
    public void EveryProvisionedAggregate_KeepsOneStatus()
    {
        var provisioned = new List<string>();
        var withoutStatus = new List<string>();
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
            if (!Regex.IsMatch(src, $@"public {type}Status Status \{{ get; private set; \}}"))
            {
                withoutStatus.Add(type);
            }
        }

        // 8 = the resources the control plane provisions to the provider (the tenant has no provider account:
        // titan-ops architecture/tenancy-and-admin.md#approval-is-activation). A scan that finds fewer is blind, not green.
        Assert.Equal(8, provisioned.Count);

        // No allowlist: a new provisioned aggregate is born with one status of its own, or this fails.
        Assert.Equal(OneStatus.OrderBy(t => t, StringComparer.Ordinal), provisioned.OrderBy(t => t, StringComparer.Ordinal));
        Assert.True(withoutStatus.Count == 0,
            "Provisioned aggregates without their own Status:\n  " + string.Join("\n  ", withoutStatus.OrderBy(t => t)));
    }

    [Fact]
    public void NoDomainType_KeepsAnExistenceAxisOrAStatePair()
    {
        var sources = ReadSources(AggregatesDir());
        Assert.True(sources.Count > 20, $"Only {sources.Count} aggregate files were read — the scan is blind.");

        var offenders = sources
            .Where(entry => AnAxis.IsMatch(entry.Source))
            .Select(entry => Path.GetFileNameWithoutExtension(entry.File))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A second axis beside the one status. Existence, the kind of the waiting operation and the move for display are "
            + "what the status says; write the status:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ThePatternForAnAxis_ReadsTheOldShapes()
    {
        Assert.Matches(AnAxis, "    public CurrentExistence CurrentExistence { get; private set; }");
        Assert.Matches(AnAxis, "    public DesiredExistence DesiredExistence { get; private set; }");
        Assert.Matches(AnAxis, "    public VmState? CurrentState { get; private set; }");
        Assert.DoesNotMatch(AnAxis, "    public Guid? CurrentAttachedVmId { get; private set; }");
    }

    // ---------- helpers ----------

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
