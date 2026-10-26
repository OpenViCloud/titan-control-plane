using System.Reflection;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The module boundary of Titan (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#module-boundary). A measurement cannot tell
// "0 by design" from "0 by luck"; these tests are the invariant.
//
// What these guards do NOT see, stated here because a green run reads like "no coupling left":
//   - The Application guard reads CONSTRUCTOR parameters only. A repository taken by method param, service
//     locator or static access is invisible to it.
//   - Both guards read TYPE references, not data flow. Two modules exchanging a bare Guid are still
//     coupled in business terms, and that is precisely how Titan wires its modules together on purpose.
//   - Nothing here constrains Titan.Infrastructure: a repository implementation MUST know its
//     aggregate, and that is the correct direction.
public class ModuleBoundaryTests
{
    // Same five names in Domain/Aggregates and Domain/Repositories (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#domain-tree).
    private static readonly string[] DomainModules =
        ["Billing", "Catalog", "Compute", "Networking", "Platform"];

    // Application has a SIXTH group, deliberately (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#admin-read-model): Admin is a
    // read-model for the admin portal with no aggregate and no repository. It reads out of Compute, Networking and
    // Platform and nothing points back at it, which keeps the dependency graph acyclic.
    //
    // Admin MUST be listed: ModuleOf returns null for a segment it does not recognise, so an unlisted group is not
    // "outside the rules", it is INVISIBLE (Guard_Sees_TheAdminReadModel_AndItsOutboundEdges).
    private static readonly string[] ApplicationModules = [.. DomainModules, "Admin"];

    [Fact]
    public void EveryRepositoryInterface_LivesInAModuleNamespace()
    {
        var repos = DomainAssembly
            .GetExportedTypes()
            .Where(t => t.IsInterface && t.Namespace is not null
                        && t.Namespace.StartsWith("Titan.Domain.Repositories", StringComparison.Ordinal))
            .ToList();

        // Without this, deleting every repository interface would make the test pass.
        Assert.NotEmpty(repos);

        var stragglers = repos
            .Where(t => t.Namespace == "Titan.Domain.Repositories")
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(stragglers.Count == 0,
            "Repository interface(s) still in the flat Titan.Domain.Repositories namespace — the "
            + "namespace IS the module label the boundary guards read, so a flat one makes them blind: "
            + string.Join(", ", stragglers));

        var wrongModule = repos
            .Where(t => t.Namespace != "Titan.Domain.Repositories")
            .Where(t => !DomainModules.Contains(t.Namespace!["Titan.Domain.Repositories.".Length..]))
            .Select(t => $"{t.Name} in {t.Namespace}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrongModule.Count == 0,
            "Repository interface(s) in a namespace that is not one of the five DOMAIN modules. Admin "
            + "is an Application-layer read-model (docs/architecture/tenancy-and-admin.md#tenant-quota); it owns no aggregate, so it owns no repository: "
            + string.Join(", ", wrongModule));
    }

    // ── https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#tenant-typed-edge: ONE typed cross-module edge is allowed, and it is Tenant ────────────────────
    //
    // A quota check must reach the tenant's caps from inside the factory of the thing being created, and a
    // Guid cannot answer "may I". Allowed, narrowly: a factory parameter of type Platform.Tenant. Not a
    // property, field, constructor or non-factory method, and no module other than Platform.
    //
    // Each row is one measured edge. Adding one is allowed; adding one silently is not.
    private static readonly Dictionary<string, string> AggregateEdgeAllowlist = new()
    {
        ["Compute.Vm.Create(tenant) -> Platform.Tenant"] =
            "US-DDD-13: the VM factory asks the tenant about VM quota, so forgetting "
            + "the check means having no VM",
        ["Compute.Volume.Create(tenant) -> Platform.Tenant"] = "US-DDD-13: volume quota",
        ["Compute.Volume.CreateFromSnapshot(tenant) -> Platform.Tenant"] =
            "US-DDD-13: a restore creates a volume, so it spends VOLUME quota",
        ["Compute.VmSnapshot.Create(tenant) -> Platform.Tenant"] = "US-DDD-13: per-VM snapshot quota",
        ["Compute.VolumeSnapshot.Create(tenant) -> Platform.Tenant"] = "US-DDD-13: per-volume snapshot quota",
        ["Networking.Network.Create(tenant) -> Platform.Tenant"] = "US-DDD-13: network and public-IPv6 quota",
        ["Networking.FirewallRule.Create(tenant) -> Platform.Tenant"] = "US-DDD-13: per-network rule quota",
        ["Compute.SshKey.Create(tenant) -> Platform.Tenant"] = "per-tenant SSH key quota",
        ["Compute.LoadBalancer.Create(tenant) -> Platform.Tenant"] = "per-tenant load balancer quota",
    };

    [Fact]
    public void DomainAggregates_HaveNoCrossModuleTypeReferences()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var (type, module) in TypesByModule(DomainAssembly, "Titan.Domain.Aggregates", DomainModules))
        {
            scanned++;
            foreach (var (referenced, where) in ReferencedTypes(type))
            {
                var other = ModuleOf(referenced, "Titan.Domain.Aggregates", DomainModules);
                if (other is not null && other != module)
                    offenders.Add($"{module}.{type.Name}.{where} -> {other}.{referenced.Name}");
            }
        }

        // A guard that scanned nothing is not a guard. 0 offenders out of 0 types reads identical to
        // 0 offenders out of 41, and only one of those is a boundary being held.
        Assert.True(scanned >= 20, $"Only {scanned} aggregate types scanned — the guard is not seeing the model.");

        var unlisted = offenders.Where(o => !AggregateEdgeAllowlist.ContainsKey(o))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(unlisted.Count == 0,
            "Aggregates reference another module by type. Cross-module references go by identity "
            + "(Guid/string id) unless there is an allowlist row saying why not — that is what keeps "
            + "the modules separable: " + string.Join(" | ", unlisted));

        // Other direction: a row for an edge that no longer exists is also a failure, or the
        // allowlist stops being a measurement (same rule as every other allowlist in this suite).
        var stale = AggregateEdgeAllowlist.Keys.Where(k => !offenders.Contains(k))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "Allowlist rows for aggregate edges that no longer exist. Delete them: "
            + string.Join(" | ", stale));
    }

    // ── https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#shared-value-types: Titan.Domain.Values is a DECLARED shared kernel, not a hole in the guard ────
    //
    // Rate is used by Catalog (offering prices) and Billing (usage records). In Titan.Domain.Values it is
    // INVISIBLE to the guard above (ModuleOf() returns null outside Titan.Domain.Aggregates.<Module>), so the
    // namespace is declared here with the property that makes it safe: a shared value type may not know
    // about any module.
    [Fact]
    public void SharedValueTypes_KnowAboutNoModule()
    {
        var values = DomainAssembly.GetExportedTypes()
            .Where(t => t.Namespace == "Titan.Domain.Values")
            .ToList();

        // 0 offenders out of 0 types reads identical to a clean scan.
        Assert.NotEmpty(values);

        var offenders = new List<string>();
        foreach (var type in values)
            foreach (var (referenced, where) in ReferencedTypes(type))
            {
                var module = ModuleOf(referenced, "Titan.Domain.Aggregates", DomainModules);
                if (module is not null)
                    offenders.Add($"Values.{type.Name}.{where} -> {module}.{referenced.Name}");
            }

        Assert.True(offenders.Count == 0,
            "A type in Titan.Domain.Values references a module. Shared value types are shared "
            + "precisely because they know nothing about who uses them — a Money that knows about "
            + "Billing is a Billing type in the wrong folder: "
            + string.Join(" | ", offenders.OrderBy(x => x, StringComparer.Ordinal)));
    }

    [Fact]
    public void ApplicationServices_CrossModuleDependencies_AreAllowlisted()
    {
        var actual = ActualApplicationEdges();

        Assert.NotEmpty(actual); // the edges are real; an empty read means the scan broke, not that they went away

        var unlisted = actual
            .Where(e => !Allowlist.ContainsKey((e.From, e.To)))
            .Select(e => $"{e.From} -> {e.To} (via {e.Via})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(unlisted.Count == 0,
            "Cross-module dependency not on the allowlist. Adding one is allowed, but it must be a "
            + "decision someone wrote down, not a side effect of a constructor edit: "
            + string.Join(" | ", unlisted));
    }

    [Fact] // an allowlist that only ever grows stops being a boundary
    public void Allowlist_HasNoStaleEntries()
    {
        var actual = ActualApplicationEdges().Select(e => (e.From, e.To)).ToHashSet();

        Assert.NotEmpty(actual);

        var stale = Allowlist.Keys
            .Where(k => !actual.Contains(k))
            .Select(k => $"{k.From} -> {k.To}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "Allowlist describes a cross-module edge that no longer exists — delete the row so the "
            + "allowlist keeps meaning what it says: " + string.Join(" | ", stale));
    }

    [Fact] // the guard must SEE Admin, not merely tolerate it
    //
    // ModuleOf returns null for an unrecognised namespace segment, so Admin missing from ApplicationModules does
    // not relax the boundary, it deletes Admin from the measurement: every boundary test stays green while its
    // handlers inject repositories of three modules.
    //
    // Assert.Contains on the module NAMES, not Assert.NotEmpty on the edge list: an empty read and a
    // read that missed exactly one module look the same from a count.
    public void Guard_Sees_TheAdminReadModel_AndItsOutboundEdges()
    {
        var scanned = TypesByModule(
                ApplicationAssembly, ApplicationPrefixes, ApplicationModules)
            .Select(x => x.Module)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Admin", scanned);

        var from = ActualApplicationEdges()
            .Where(e => e.From == "Admin")
            .Select(e => e.To)
            .ToHashSet(StringComparer.Ordinal);

        // The Admin handlers read outward into exactly these three. If they grow a fourth, the allowlist test
        // fails first; if they lose one, Allowlist_HasNoStaleEntries fails.
        Assert.Equal(new[] { "Compute", "Networking", "Platform" }.ToHashSet(StringComparer.Ordinal), from);
    }

    [Fact]
    public void Allowlist_HasNoDebtRowsLeft()
    {
        var debt = Allowlist
            .Where(kv => kv.Value.Contains("DEBT:", StringComparison.Ordinal))
            .Select(kv => $"{kv.Key.From} -> {kv.Key.To}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(debt.Count == 0,
            "Allowlist still carries DEBT row(s): " + string.Join(" | ", debt));
    }

    [Fact] // checked rather than eyeballed: a prose table of edges cannot fail, this can
    public void DependencyGraph_HasNoCycles()
    {
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in ActualApplicationEdges())
        {
            if (e.From == e.To) continue;
            (graph.TryGetValue(e.From, out var to) ? to : graph[e.From] = new(StringComparer.Ordinal))
                .Add(e.To);
        }

        Assert.NotEmpty(graph); // an empty graph is trivially acyclic and proves nothing

        var cycles = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unseen, 1 on stack, 2 done

        void Visit(string node, List<string> path)
        {
            state[node] = 1;
            path.Add(node);
            var neighbours = graph.TryGetValue(node, out var to)
                ? to.OrderBy(x => x, StringComparer.Ordinal).ToList()
                : [];
            foreach (var next in neighbours)
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    var from = path.IndexOf(next);
                    cycles.Add(string.Join(" -> ", path.Skip(from).Append(next)));
                }
                else if (state.GetValueOrDefault(next) == 0)
                {
                    Visit(next, path);
                }
            }
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }

        foreach (var node in graph.Keys.OrderBy(x => x, StringComparer.Ordinal))
            if (state.GetValueOrDefault(node) == 0) Visit(node, []);

        Assert.True(cycles.Count == 0,
            "Cross-module dependency graph has cycle(s). A cycle means the two modules cannot be "
            + "reasoned about, released or extracted separately — 'module' stops meaning anything: "
            + string.Join(" | ", cycles.Distinct(StringComparer.Ordinal)));
    }

    // ---- allowlist -------------------------------------------------------------------------
    //
    // Every row is a cross-module constructor dependency that exists on purpose. The reason is not
    // decoration: it is what a future reader needs in order to decide whether the row may be deleted.
    private static readonly Dictionary<(string From, string To), string> Allowlist = new()
    {
        [("Billing", "Catalog")] =
            "The three meters rate usage at the offering's price (docs/architecture/money-and-wallet.md#price-belongs-to-offering: price belongs to the offering).",
        [("Billing", "Compute")] =
            "The three meters read what is currently running (Vm, Volume, snapshots) to emit usage records.",
        [("Billing", "Platform")] =
            "GetReceivablesQueryHandler reads tenant names and debt ceilings for the receivables view "
            + "(docs/architecture/money-and-wallet.md#postpaid-no-brake DEBT-6); SettleInvoiceFromWalletCommandHandler "
            + "writes an admin audit entry when an admin spends a tenant's wallet (docs/architecture/tenancy-and-admin.md#admin-audit).",
        [("Catalog", "Platform")] =
            "UpdateVmOfferingPriceCommandHandler writes an admin audit entry when a price changes "
            + "(docs/architecture/money-and-wallet.md#price-belongs-to-offering). CQRS-5 moved it out of VmOfferingService; the edge and its reason did not change.",
        [("Compute", "Catalog")] =
            "CreateVm resolves template and offering; the offering is the sole source of sizing (docs/features/catalog/design.md#template-borrows-sizing). "
            + "CreateVolume refuses an offering the catalog lacks, or the disk meter would meet a volume with no price "
            + "(docs/features/volume/design.md#offering-must-exist).",
        [("Compute", "Networking")] =
            "A VM and a load balancer are created onto a network — the id alone is not enough, the network (its state, "
            + "its public /64) must be verified.",
        [("Compute", "Platform")] =
            "The VM, volume, VM snapshot, volume snapshot, SSH key and load balancer create handlers need the tenant: "
            + "every resource cap lives on it (docs/architecture/tenancy-and-admin.md#tenant-quota), and the create handlers and VM delete read its ProjectName (docs/architecture/tenancy-and-admin.md#project-name).",
        [("Networking", "Platform")] =
            "CreateNetworkCommandHandler needs the tenant for quota and its ProjectName, and the zone's cluster for the "
            + "uplink and IPv6 block a public /64 comes from (docs/infra/incus/design.md#ipv6-address-plan); "
            + "CreateFirewallRuleCommandHandler needs it for the per-network rule cap (docs/architecture/tenancy-and-admin.md#tenant-quota).",

        // Admin reads outward into three modules and nothing reads back into Admin
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#admin-read-model), which is why DependencyGraph_HasNoCycles can
        // hold. That test, not this comment, is the guarantee.
        [("Admin", "Compute")] =
            "ListAdminVmsQueryHandler lists VMs ACROSS tenants for the admin portal, and "
            + "DeleteAdminVmCommandHandler deletes one (docs/architecture/tenancy-and-admin.md#admin-cross-tenant-write), reading snapshots for the in-progress "
            + "guard and the attached volumes it detaches. A separate path on purpose: the tenant Vm handlers require a tenant "
            + "(RequireTenantId), so reusing them would mean growing a bypass flag on the tenant path. "
            + "ScheduleBackupsCommandHandler and ListDiskBackupsQueryHandler read every tenant's VMs and volumes for the platform's "
            + "backups (docs/features/backup/design.md#incus-backup): Platform cannot, Compute already reads Platform.",
        [("Admin", "Networking")] =
            "ListAdminNetworksQueryHandler lists networks across tenants for the admin portal (docs/architecture/tenancy-and-admin.md#admin-cross-tenant-read).",
        [("Admin", "Platform")] =
            "DeleteAdminVmCommandHandler resolves the owning tenant's provider account and writes an "
            + "admin audit entry for every cross-tenant delete (docs/architecture/tenancy-and-admin.md#admin-cross-tenant-write). "
            + "ScheduleBackupsCommandHandler names each tenant's Incus project; ListDiskBackupsQueryHandler reads the backup cache "
            + "and its health rule (docs/features/backup/design.md#incus-backup).",
    };

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#guard-sees-every-module — the anti-false-green check for the prefix table itself.
    // Every "0 offenders" in this file is computed over whatever TypesByModule saw; if ApplicationPrefixes drifted
    // from the real tree, the zeros would stay zeros over an empty set. So the scan must reach every module by name.
    [Fact]
    public void EveryModule_IsSeenByTheScan()
    {
        var byModule = TypesByModule(ApplicationAssembly, ApplicationPrefixes, ApplicationModules)
            .GroupBy(x => x.Module)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var module in ApplicationModules)
            Assert.True(byModule.TryGetValue(module, out var n) && n > 0,
                $"The scan found no types in module '{module}'. Either the module is gone, or "
                + "ApplicationPrefixes no longer matches the folder tree — and the second case makes "
                + "every other test in this file pass while measuring nothing (docs/architecture/tenancy-and-admin.md#tenant-quota).");

        // Two named types from two different modules, so a prefix that collapsed everything into one
        // bucket fails here rather than quietly relabelling every edge.
        var typed = TypesByModule(ApplicationAssembly, ApplicationPrefixes, ApplicationModules)
            .ToDictionary(x => x.Type, x => x.Module);
        Assert.Equal("Compute", typed[typeof(Titan.Application.Queries.Compute.Vm.ListVmsQueryHandler)]);
        Assert.Equal("Billing", typed[typeof(Titan.Application.Services.Billing.TopUp.TopUpConfirmation)]);
        // A module-level Services/ type too (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#services-branch), so the third branch is
        // proven readable, not just compiled.
        Assert.Equal("Compute", typed[typeof(Titan.Application.Services.Compute.ComputeOwnership)]);
    }

    // ---- machinery -------------------------------------------------------------------------

    private static Assembly DomainAssembly => typeof(NotFoundException).Assembly;
    private static Assembly ApplicationAssembly => typeof(Titan.Application.Queries.Compute.Vm.ListVmsQueryHandler).Assembly;

    private record Edge(string From, string To, string Via);

    // Getting this list wrong does not produce a failure, it produces SILENCE: ModuleOf returns null for a
    // namespace it does not recognise, so every test in this file goes on reporting zero offenders computed
    // over nothing (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#guard-sees-every-module). EveryModule_IsSeenByTheScan
    // is what stands in the way.
    private static readonly string[] ApplicationPrefixes = ["Titan.Application"];

    private static List<Edge> ActualApplicationEdges()
    {
        var edges = new List<Edge>();

        foreach (var (type, module) in TypesByModule(ApplicationAssembly, ApplicationPrefixes, ApplicationModules))
        {
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var param in ctor.GetParameters())
                {
                    foreach (var referenced in Unwrap(param.ParameterType))
                    {
                        var target = ModuleOf(referenced, "Titan.Domain.Repositories", DomainModules)
                                     ?? ModuleOf(referenced, ApplicationPrefixes, ApplicationModules);
                        if (target is not null && target != module)
                            edges.Add(new Edge(module, target, $"{type.Name}.{param.Name}"));
                    }
                }
            }
        }

        return edges;
    }

    private static IEnumerable<(Type Type, string Module)> TypesByModule(
        Assembly asm, string[] prefixes, string[] modules)
    {
        foreach (var type in asm.GetExportedTypes())
        {
            var module = ModuleOf(type, prefixes, modules);
            if (module is not null) yield return (type, module);
        }
    }

    // First prefix that matches wins. Services/ is a branch (see Branches), so a type in
    // Services/Billing/ is read as Billing, not as Services.
    private static string? ModuleOf(Type type, string[] prefixes, string[] modules)
    {
        foreach (var prefix in prefixes)
        {
            var module = ModuleOf(type, prefix, modules);
            if (module is not null) return module;
        }
        return null;
    }

    private static IEnumerable<(Type Type, string Module)> TypesByModule(
        Assembly asm, string prefix, string[] modules)
    {
        foreach (var type in asm.GetExportedTypes())
        {
            var module = ModuleOf(type, prefix, modules);
            if (module is not null) yield return (type, module);
        }
    }

    // Returns the module segment if the type sits under "<prefix>.<Module>", else null. It matches the segment
    // right after the prefix, because Domain aggregates nest one level deeper (Aggregates/<Module>/<Aggregate>/).
    // Under a branch the module is the segment AFTER it (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#application-tree):
    // Titan.Application.Queries.Compute.Vm is Compute, Titan.Application.Services.Billing.TopUp is Billing. The
    // shorter Titan.Application.Billing is still read, so a type dropped back at the root stays measured.
    //
    // A branch missing from this list does not fail, it goes SILENT: ModuleOf returns null for every type under
    // it, and Allowlist_HasNoStaleEntries reports live edges as gone. EveryModule_IsSeenByTheScan catches it.
    private static readonly string[] Branches = ["Commands", "Queries", "Services"];

    private static string? ModuleOf(Type type, string prefix, string[] modules)
    {
        var ns = type.Namespace;
        if (ns is null || !ns.StartsWith(prefix + ".", StringComparison.Ordinal)) return null;
        var segments = ns[(prefix.Length + 1)..].Split('.');
        var head = segments[0];
        if (Branches.Contains(head) && segments.Length > 1) head = segments[1];
        return modules.Contains(head) ? head : null;
    }

    private static IEnumerable<(Type Type, string Where)> ReferencedTypes(Type type)
    {
        foreach (var p in type.GetProperties())
            foreach (var t in Unwrap(p.PropertyType)) yield return (t, p.Name);

        foreach (var f in type.GetFields())
            foreach (var t in Unwrap(f.FieldType)) yield return (t, f.Name);

        foreach (var c in type.GetConstructors())
            foreach (var p in c.GetParameters())
                foreach (var t in Unwrap(p.ParameterType)) yield return (t, $"ctor({p.Name})");

        foreach (var m in type.GetMethods().Where(m => m.DeclaringType == type))
        {
            foreach (var t in Unwrap(m.ReturnType)) yield return (t, $"{m.Name}()");
            foreach (var p in m.GetParameters())
                foreach (var t in Unwrap(p.ParameterType)) yield return (t, $"{m.Name}({p.Name})");
        }
    }

    // List<Vm> and Task<IReadOnlyList<Vm>> both have to resolve to Vm, or the guard reads as clean
    // while a collection quietly crosses the boundary.
    private static IEnumerable<Type> Unwrap(Type t)
    {
        if (t.IsByRef || t.IsArray) t = t.GetElementType() ?? t;
        yield return t;
        if (t.IsGenericType)
            foreach (var arg in t.GetGenericArguments())
                foreach (var inner in Unwrap(arg)) yield return inner;
    }
}
