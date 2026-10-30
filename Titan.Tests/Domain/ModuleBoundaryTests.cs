using System.Reflection;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The boundaries of Titan (titan-ops architecture/code-structure.md#module-boundary): in Titan.Domain between aggregates,
// in Titan.Application between modules. A measurement cannot tell "0 by design" from "0 by luck"; these tests are the
// invariant.
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
    // The module groups of Titan.Application (titan-ops architecture/code-structure.md#application-tree). Titan.Domain
    // has no module layer: its aggregates sit side by side (titan-ops architecture/code-structure.md#domain-tree).
    //
    // Admin is a SIXTH group, deliberately (titan-ops architecture/code-structure.md#admin-read-model): a read-model for
    // the admin portal with no aggregate and no repository. It reads out of Compute, Networking and Platform and
    // nothing points back at it, which keeps the dependency graph acyclic. It MUST be listed: ModuleOf returns null
    // for a segment it does not recognise, so an unlisted group is not "outside the rules", it is INVISIBLE
    // (Guard_Sees_TheAdminReadModel_AndItsOutboundEdges).
    private static readonly string[] ApplicationModules =
        ["Billing", "Catalog", "Compute", "Networking", "Platform", "Admin"];

    // The Application module each repository belongs to, read by the Application guard to label a handler's edge
    // through a repository. A table, not a folder: Titan.Domain.Repositories is flat, and six repositories have no
    // Application folder of their aggregate's name (ICreditRepository serves TopUp, for one).
    private static readonly Dictionary<string, string> RepositoryModule = new(StringComparer.Ordinal)
    {
        ["ICreditRepository"] = "Billing",
        ["IInvoiceRepository"] = "Billing",
        ["IPaymentGatewayTransactionRepository"] = "Billing",
        ["IUsageRecordRepository"] = "Billing",
        ["IWalletMutationRepository"] = "Billing",
        ["IWalletRepository"] = "Billing",
        ["ISnapshotOfferingRepository"] = "Catalog",
        ["IVmOfferingRepository"] = "Catalog",
        ["IVmTemplateRepository"] = "Catalog",
        ["IVolumeOfferingRepository"] = "Catalog",
        ["ILoadBalancerRepository"] = "Compute",
        ["ISshKeyRepository"] = "Compute",
        ["IVmRepository"] = "Compute",
        ["IVmSnapshotRepository"] = "Compute",
        ["IVolumeRepository"] = "Compute",
        ["IVolumeSnapshotRepository"] = "Compute",
        ["IFirewallRuleRepository"] = "Networking",
        ["INetworkRepository"] = "Networking",
        ["IAcceptanceRepository"] = "Platform",
        ["IActivityEventRepository"] = "Platform",
        ["IAdminAuditLogRepository"] = "Platform",
        ["IApiTokenRepository"] = "Platform",
        ["IBackupObservationRepository"] = "Platform",
        ["IBackupRecordRepository"] = "Platform",
        ["IProviderCommandRepository"] = "Platform",
        ["ITenantRepository"] = "Platform",
    };

    [Fact]
    public void EveryRepositoryInterface_HasAModuleRow()
    {
        var repos = DomainAssembly
            .GetExportedTypes()
            .Where(t => t.IsInterface && t.Namespace is not null
                        && t.Namespace.StartsWith("Titan.Domain.Repositories", StringComparison.Ordinal))
            .ToList();

        // Without this, deleting every repository interface would make the test pass.
        Assert.NotEmpty(repos);

        var nested = repos
            .Where(t => t.Namespace != "Titan.Domain.Repositories")
            .Select(t => $"{t.Name} in {t.Namespace}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(nested.Count == 0,
            "Repository interface(s) below Titan.Domain.Repositories. The namespace is flat; the module a handler "
            + "reaches through a repository is RepositoryModule's row, not a folder: " + string.Join(", ", nested));

        var names = repos.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var missing = names.Where(n => !RepositoryModule.ContainsKey(n)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var stale = RepositoryModule.Keys.Where(n => !names.Contains(n)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unknown = RepositoryModule.Where(kv => !ApplicationModules.Contains(kv.Value) || kv.Value == "Admin")
            .Select(kv => $"{kv.Key} -> {kv.Value}").OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && stale.Count == 0 && unknown.Count == 0,
            "RepositoryModule out of step with the repositories. Without a row the Application guard cannot see edges "
            + "through that repository. Missing: " + string.Join(", ", missing)
            + " | stale: " + string.Join(", ", stale)
            + " | not a module that owns an aggregate (Admin owns none): " + string.Join(", ", unknown));
    }

    // ── titan-ops architecture/code-structure.md#tenant-typed-edge: aggregates reference each other by id ──────────────
    //
    // A typed reference from one aggregate's public surface (property, field, constructor, method) to another
    // aggregate's type needs a row here saying why an id is not enough. The Tenant rows are the quota rule: a quota
    // check must reach the tenant's caps from inside the factory of the thing being created, and a Guid cannot
    // answer "may I".
    //
    // Each row is one measured edge. Adding one is allowed; adding one silently is not.
    private static readonly Dictionary<string, string> AggregateEdgeAllowlist = new(StringComparer.Ordinal)
    {
        ["Vm.Create(tenant) -> Tenant"] =
            "the VM factory asks the tenant about VM quota, so forgetting the check means having no VM",
        ["Volume.Create(tenant) -> Tenant"] = "volume quota",
        ["Volume.CreateFromSnapshot(tenant) -> Tenant"] = "a restore creates a volume, so it spends VOLUME quota",
        ["Vm.AddSnapshot(tenant) -> Tenant"] = "per-VM snapshot quota, counted on the snapshots the VM holds (its child entities)",
        ["Volume.AddSnapshot(tenant) -> Tenant"] = "per-volume snapshot quota, counted on the snapshots the volume holds (its child entities)",
        ["Network.Create(tenant) -> Tenant"] = "network and public-IPv6 quota",
        ["Network.AddFirewallRule(tenant) -> Tenant"] = "per-network rule quota, counted on the rules the network holds (its child entities)",
        ["SshKey.Create(tenant) -> Tenant"] = "per-tenant SSH key quota",
        ["LoadBalancer.Create(tenant) -> Tenant"] = "per-tenant load balancer quota",

        // A rule that spans two aggregates is checked by the one being changed, on the other's state.
        ["LoadBalancer.Create(backends) -> Vm"] =
            "each backend must be the tenant's VM and accept the network (Vm.BeginLoadBalancerBackend), checked in the factory",
        ["LoadBalancer.RequestUpdate(backends) -> Vm"] = "the same backend rule, on an update",
        ["Volume.Attach(vm) -> Vm"] =
            "the VM's side of an attach (Vm.BeginVolumeAttach) is checked before the provider ids are handed out",

        // Not a rule on another aggregate's state: an enum another aggregate owns, used as a value.
        ["InvoiceItem.Create(resourceType) -> UsageResourceType"] =
            "an invoice line is a usage line of one resource type, the enum UsageRecord owns",
        ["InvoiceItem.ResourceType -> UsageResourceType"] = "the same enum, stored on the line",
        ["InvoiceItem.get_ResourceType() -> UsageResourceType"] = "the property's getter, seen as a method",
    };

    [Fact]
    public void DomainAggregates_ReferenceOtherAggregatesOnlyThroughTheAllowlist()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetExportedTypes())
        {
            var aggregate = AggregateOf(type);
            if (aggregate is null) continue;
            scanned++;
            foreach (var (referenced, where) in ReferencedTypes(type))
            {
                var other = AggregateOf(referenced);
                if (other is not null && other != aggregate)
                    offenders.Add($"{type.Name}.{where} -> {referenced.Name}");
            }
        }

        // A guard that scanned nothing is not a guard. 0 offenders out of 0 types reads identical to
        // 0 offenders out of 41, and only one of those is a boundary being held.
        Assert.True(scanned >= 20, $"Only {scanned} aggregate types scanned — the guard is not seeing the model.");

        var unlisted = offenders.Distinct(StringComparer.Ordinal).Where(o => !AggregateEdgeAllowlist.ContainsKey(o))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(unlisted.Count == 0,
            "An aggregate references another aggregate by type. References between aggregates go by identity "
            + "(Guid/string id) unless there is an allowlist row saying why not: " + string.Join(" | ", unlisted));

        // Other direction: a row for an edge that no longer exists is also a failure, or the
        // allowlist stops being a measurement (same rule as every other allowlist in this suite).
        var stale = AggregateEdgeAllowlist.Keys.Where(k => !offenders.Contains(k))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "Allowlist rows for aggregate edges that no longer exist. Delete them: "
            + string.Join(" | ", stale));
    }

    // ── titan-ops architecture/code-structure.md#shared-value-types: Titan.Domain.Values is a DECLARED shared kernel, not a hole in the guard ────
    //
    // Rate is used by the offerings and by UsageRecord. In Titan.Domain.Values it is INVISIBLE to the guard above
    // (AggregateOf() returns null outside Titan.Domain.Aggregates.<Aggregate>), so the namespace is declared here
    // with the property that makes it safe: a shared value type may not know about any aggregate.
    [Fact]
    public void SharedValueTypes_KnowAboutNoAggregate()
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
                var aggregate = AggregateOf(referenced);
                if (aggregate is not null)
                    offenders.Add($"Values.{type.Name}.{where} -> {aggregate}.{referenced.Name}");
            }

        Assert.True(offenders.Count == 0,
            "A type in Titan.Domain.Values references an aggregate. Shared value types are shared "
            + "precisely because they know nothing about who uses them — a Rate that knows about "
            + "VmOffering is a VmOffering type in the wrong folder: "
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
            "CreateNetworkCommandHandler needs the tenant for quota and its ProjectName, and the cluster for the "
            + "uplink and IPv6 block a public /64 comes from (docs/infra/incus/design.md#ipv6-address-plan); "
            + "CreateFirewallRuleCommandHandler needs it for the per-network rule cap (docs/architecture/tenancy-and-admin.md#tenant-quota).",

        // Admin reads outward into three modules and nothing reads back into Admin
        // (titan-ops architecture/code-structure.md#admin-read-model), which is why DependencyGraph_HasNoCycles can
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

    // titan-ops architecture/code-structure.md#guard-sees-every-module — the anti-false-green check for the prefix table itself.
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
        // A module-level Services/ type too (titan-ops architecture/code-structure.md#services-branch), so the third branch is
        // proven readable, not just compiled.
        Assert.Equal("Compute", typed[typeof(Titan.Application.Services.Compute.ComputeOwnership)]);
    }

    // ---- machinery -------------------------------------------------------------------------

    private static Assembly DomainAssembly => typeof(NotFoundException).Assembly;
    private static Assembly ApplicationAssembly => typeof(Titan.Application.Queries.Compute.Vm.ListVmsQueryHandler).Assembly;

    private record Edge(string From, string To, string Via);

    // Getting this list wrong does not produce a failure, it produces SILENCE: ModuleOf returns null for a
    // namespace it does not recognise, so every test in this file goes on reporting zero offenders computed
    // over nothing (titan-ops architecture/code-structure.md#guard-sees-every-module). EveryModule_IsSeenByTheScan
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
                        var target = RepositoryModuleOf(referenced)
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

    // The aggregate a Domain type belongs to: the segment after Titan.Domain.Aggregates, else null.
    private static string? AggregateOf(Type type)
    {
        const string prefix = "Titan.Domain.Aggregates.";
        var ns = type.Namespace;
        if (ns is null || !ns.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return ns[prefix.Length..].Split('.')[0];
    }

    // The module of a repository interface, from RepositoryModule; null for any other type.
    private static string? RepositoryModuleOf(Type type)
    {
        if (type.Namespace != "Titan.Domain.Repositories") return null;
        return RepositoryModule.TryGetValue(type.Name, out var module) ? module : null;
    }

    // Returns the module segment if the type sits under "<prefix>.<Module>", else null. Under a branch the module
    // is the segment AFTER it (titan-ops architecture/code-structure.md#application-tree):
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
