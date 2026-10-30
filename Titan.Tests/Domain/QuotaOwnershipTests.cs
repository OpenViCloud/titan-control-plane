using System.Text.RegularExpressions;

namespace Titan.Tests.Domain;

// WHERE the quota rule lives (titan-ops architecture/tenancy-and-admin.md#tenant-quota) is a placement invariant, and
// placement is not observable from behaviour, so these guards read source text. A service that re-grew its own
// `const int MaxVolumes = 10` would pass every behavioural test in the suite.
//
// What they do NOT see, stated because a green run reads like "quota can only live on Tenant":
//   - a cap read from configuration, or passed in as a method argument, is invisible here
//   - they match TEXT. A constant written as `10` inline, or computed, slips through
//   - they say nothing about whether the caller remembered to ASK the tenant at all
public class QuotaOwnershipTests
{
    private const string ApplicationDir = "Titan.Application";

    // Folders under Titan.Application that are NOT modules. Named so the exclusion is visible rather
    // than implied — and so that adding one is a decision, not an accident.
    //
    // The branches are here for a different reason than the others (titan-ops architecture/code-structure.md#application-tree):
    // they ARE scanned, but as BRANCHES whose module sits one level in (Commands/Compute/Vm), not as modules.
    // A branch missing from this list is read as a module name (TheScanReachesTheHandlerTree turns red).
    private static readonly string[] Branches = ["Commands", "Queries", "Services"];
    private static readonly string[] NonModuleDirs =
        ["Abstractions", "Options", "bin", "obj", .. Branches];

    // Page-size caps, not tenant resource quota. ListAdminAuditLogQueryHandler.MaxLimit=200 and
    // ListActivityEventsQueryHandler.MaxLimit=50 bound how many rows one API response may carry; they are not
    // something an admin would ever set per customer. Excluded by name so the exclusion is visible, not implied.
    private static readonly string[] AllowedConstants = ["MaxLimit"];

    private static IEnumerable<string> ModuleDirs() =>
        Directory
            .EnumerateDirectories(Path.Combine(RepoRoot.ControlPlane, ApplicationDir))
            .Where(d => !NonModuleDirs.Contains(Path.GetFileName(d)));

    // Any module folder left at the root PLUS the three branches: a quota constant compiled into a
    // handler is exactly as wrong as one compiled into a service, so every tree is read.
    private static IEnumerable<string> ServiceFiles() =>
        ModuleDirs()
            .Concat(Branches.Select(b => Path.Combine(RepoRoot.ControlPlane, ApplicationDir, b)))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories));

    [Fact]
    public void NoApplicationService_HoldsItsOwnQuotaConstant()
    {
        var files = ServiceFiles().ToList();
        Assert.NotEmpty(files); // 0 files scanned reads identical to 0 offenders found

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var m in Regex.Matches(File.ReadAllText(file),
                         @"const\s+int\s+(\w*(?:Max|Quota)\w*)\s*=\s*(\d+)"))
            {
                var match = (Match)m;
                var name = match.Groups[1].Value;
                if (AllowedConstants.Contains(name)) continue;
                offenders.Add($"{Path.GetFileName(file)}: const int {name} = {match.Groups[2].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Application service holds its own resource cap. Caps belong to TenantQuota (docs/architecture/tenancy-and-admin.md#tenant-quota) so "
            + "one customer's limits are one row, not five compiled-in numbers: "
            + string.Join(" | ", offenders));
    }

    [Fact]
    public void NoApplicationService_ComparesAgainstATenantCap()
    {
        var files = ServiceFiles().ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var src = File.ReadAllText(file);
            // `something >= tenant.Quota.MaxX` / `>= tenant.MaxX`
            foreach (var m in Regex.Matches(src, @"[<>]=?\s*\w+\.(?:Quota\.)?Max\w+"))
                offenders.Add($"{Path.GetFileName(file)}: {((Match)m).Value.Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "Application service compares against a tenant cap directly. The service COUNTS, the "
            + "tenant DECIDES (docs/architecture/tenancy-and-admin.md#tenant-quota) — call tenant.EnsureCanCreateX(count) instead: "
            + string.Join(" | ", offenders));
    }

    // ── the check must be UNREACHABLE from a service, not merely available ────────
    //
    // A check the caller can skip is advice. As in titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id,
    // the aggregate hands out the ONE thing the caller cannot proceed without; for creation that is the entity
    // itself, so the check lives INSIDE the factory (titan-ops architecture/code-structure.md#tenant-typed-edge).
    //
    // Two guards, deliberately measuring different things. The text one below can be fooled by an
    // indirection; the reflection one cannot, but it only sees the surface Titan.Application is
    // compiled against. Neither alone is the invariant.

    [Fact] // text guard
    public void NoApplicationService_AsksTheTenantAboutQuota()
    {
        var files = ServiceFiles().ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = Regex.Replace(File.ReadAllText(file), @"//.*$", "", RegexOptions.Multiline);
            foreach (var m in Regex.Matches(code, @"\.(EnsureCanCreate\w+|EnsureCanEnable\w+)\("))
                offenders.Add($"{Path.GetFileName(file)}: {((Match)m).Groups[1].Value}");
        }

        Assert.True(offenders.Count == 0,
            "An application service still asks the tenant for permission. A check the caller has to "
            + "remember is advice, not an invariant (US-DDD-13) — pass the tenant and the count to the "
            + "FACTORY instead, so skipping the check means having no entity: "
            + string.Join(" | ", offenders));
    }

    [Fact] // reflection guard: the surface is not there to be called
    public void TenantExposesNoPublicQuotaCheck()
    {
        var tenant = typeof(Titan.Domain.Aggregates.Tenant.Tenant);

        var publicChecks = tenant
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("EnsureCan", StringComparison.Ordinal))
            .Select(m => m.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // The guard has to see the type it is asserting about; a renamed aggregate would otherwise
        // make this pass by finding nothing.
        Assert.NotEmpty(tenant.GetMethods(System.Reflection.BindingFlags.Public
                                          | System.Reflection.BindingFlags.Instance));

        Assert.True(publicChecks.Count == 0,
            "Tenant still exposes a quota check to anything outside Titan.Domain. Make it internal: "
            + "Titan.Domain grants InternalsVisibleTo to Titan.Tests only, so internal means the "
            + "COMPILER stops Titan.Application from calling it — which is stronger than this test: "
            + string.Join(", ", publicChecks));
    }

    [Fact] // the guards above are only meaningful if the scan reaches the real application tree
    public void TheScanReachesEveryServiceModule()
    {
        // The service modules sit one level in, under Services/ (titan-ops architecture/code-structure.md#services-branch).
        var dirs = Directory
            .EnumerateDirectories(Path.Combine(RepoRoot.ControlPlane, ApplicationDir, "Services"))
            .Select(Path.GetFileName)
            .ToHashSet();

        foreach (var module in new[] { "Billing", "Catalog", "Compute", "Networking", "Platform" })
            Assert.Contains(module, dirs);
    }

    // The test above only asks "does a directory named Compute exist?", which cannot tell a module that is all
    // there from one that is half moved. This names a file from each branch: the scan has to reach the handlers.
    [Fact]
    public void TheScanReachesTheHandlerTree()
    {
        var files = ServiceFiles().Select(Path.GetFileName).ToHashSet();

        Assert.Contains("CreateVmCommandHandler.cs", files);   // Commands/Compute/Vm
        Assert.Contains("ListVmsQueryHandler.cs", files);      // Queries/Compute/Vm
        Assert.Contains("ComputeOwnership.cs", files);         // Services/Compute

        // And the branches are not mistaken for modules.
        Assert.DoesNotContain("Commands", ModuleDirs().Select(Path.GetFileName));
        Assert.DoesNotContain("Queries", ModuleDirs().Select(Path.GetFileName));
        Assert.DoesNotContain("Services", ModuleDirs().Select(Path.GetFileName));
    }
}
