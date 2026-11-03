using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#shape: every aggregate on the request shape has the same four methods, its
// Mark* return nothing, a report with a foreign id changes nothing, every lifecycle handler locks the row, and every
// command payload carries the request id. Reflection and a factory for the first three; source text for the last two.
//
// What this is SILENT about: a lock taken by another name, a lock on another row (create locks its network), a payload built outside ProviderCommandQueue, and the
// semantics of EndRequest beyond "a foreign id is ignored" (VmRequestLifecycleTests, VolumeRequestLifecycleTests,
// NetworkRequestLifecycleTests, VmSnapshotRequestLifecycleTests, LoadBalancerRequestLifecycleTests and
// VmTemplateSyncRequestLifecycleTests hold those). A child entity (VolumeSnapshot, FirewallRule, VmSnapshot,
// VmTemplateCluster) is on the shape with internal methods, reached through its root.
public class RequestLifecycleShapeTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // Aggregates on the new shape: the ones with a Pending property (titan-ops architecture/resource-lifecycle.md#rollout).
    private static List<Type> OnTheRequestShape() =>
        typeof(Vm).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && t.Namespace is not null && t.Namespace.StartsWith("Titan.Domain.Aggregates.", StringComparison.Ordinal)
                        && t.GetProperty("Pending", BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToList();

    // Each rollout phase adds its aggregate here, with the factory that builds one waiting on a request.
    private static readonly Dictionary<Type, Func<object>> WaitingOnARequest = new()
    {
        [typeof(Vm)] = () => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20),
        [typeof(Volume)] = () => Volume.Create(TestTenant.WithRoomToSpare().Id, "shape", "cluster-a", "std", 10),
        [typeof(VolumeSnapshot)] = () =>
        {
            Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, "shape", "cluster-a", "std", 10);
            volume.EndRequest(volume.Pending!.Id, VolumeRequestEnd.Done("vol-shape", null));
            return volume.AddSnapshot("shape").Snapshot;
        },
        [typeof(VmSnapshot)] = () =>
        {
            Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20);
            vm.EndRequest(vm.Pending!.Id, VmRequestEnd.Done("vm-shape", new VmObservation(VmState.Running, "10.0.0.9", null)));
            return vm.AddSnapshot("shape").Snapshot;
        },
        [typeof(Network)] = () => ANetwork(),
        [typeof(LoadBalancer)] = () =>
        {
            const string subnet = "2001:db8:0:9::/64";
            Guid tenantId = TestTenant.WithRoomToSpare().Id;
            Guid networkId = Guid.NewGuid();
            Vm backend = Vm.Create(tenantId, networkId, "shape", "m", "tpl", "cluster-a", 20);
            backend.EndRequest(backend.Pending!.Id, VmRequestEnd.Done("vm-shape", new VmObservation(VmState.Running, "10.0.0.9", null)));
            LoadBalancer lb = LoadBalancer.Create(tenantId, "shape", networkId, "net-shape", subnet, "cluster-a",
                LoadBalancerVip.For(subnet, 1), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [backend]);
            lb.EndRequest(lb.Pending!.Id, LoadBalancerRequestEnd.Done(lb.Vip));
            lb.RequestUpdate([new LoadBalancerListenerSpec(LoadBalancerProtocol.UDP, 53, 53)], [backend]);
            return lb;
        },
        [typeof(FirewallRule)] = () =>
        {
            Network network = ANetwork();
            network.EndRequest(network.Pending!.Id,
                NetworkRequestEnd.Done("net-shape", new NetworkObservation(NetworkStatus.Created, "10.9.0.0/24", "10.9.0.1")));
            return network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8")).Rule;
        },
        [typeof(VmTemplateCluster)] = () =>
        {
            VmTemplate template = VmTemplate.Create("shape", null, "ubuntu/22.04", 10, "m");
            template.RequestSyncIn("cluster-a");
            return template.Clusters.Single();
        },
    };

    private static Network ANetwork() =>
        Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("shape"), "cluster-a", null, wantsPublicIpv6: false);

    [Fact]
    public void EveryAggregateOnTheShape_HasTheFourMethods_AndIsKnownHere()
    {
        List<Type> shaped = OnTheRequestShape();
        Assert.NotEmpty(shaped);
        Assert.Equal(shaped.OrderBy(t => t.Name), WaitingOnARequest.Keys.OrderBy(t => t.Name));

        var missing = new List<string>();
        foreach (Type type in shaped)
        {
            string[] names = type.GetMethods(Declared).Select(m => m.Name).Distinct().ToArray();
            foreach (string required in new[] { "EndRequest", "MarkObserved", "MarkNotFound" })
            {
                if (!names.Contains(required)) missing.Add($"{type.Name}.{required}");
            }
            if (!names.Any(n => n.StartsWith("Request", StringComparison.Ordinal))) missing.Add($"{type.Name}.Request*");
        }
        Assert.True(missing.Count == 0, "Aggregates on the request shape without the four methods: " + string.Join(", ", missing));
    }

    [Fact] // a Mark* that returns something is a Mark* that ends a request somewhere
    public void MarkMethods_ReturnNothing()
    {
        var offenders = OnTheRequestShape()
            .SelectMany(t => t.GetMethods(Declared).Where(m => m.Name.StartsWith("Mark", StringComparison.Ordinal)))
            .Where(m => m.ReturnType != typeof(void))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name} returns {m.ReturnType.Name}")
            .ToList();
        Assert.True(offenders.Count == 0, "Mark* must return void (titan-ops architecture/resource-lifecycle.md#shape): " + string.Join(", ", offenders));
    }

    [Fact] // run for real, through each type's factory: a report naming another request leaves every public property as it was
    public void EndRequest_WithAForeignId_ReturnsNull_AndChangesNoPublicProperty()
    {
        foreach ((Type type, Func<object> build) in WaitingOnARequest)
        {
            object aggregate = build();
            MethodInfo endRequest = type.GetMethod("EndRequest", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            object end = ADoneReportFor(type);
            Dictionary<string, object?> before = PublicProperties(aggregate);

            object? result = endRequest.Invoke(aggregate, [Guid.NewGuid(), end]);

            Assert.Null(result);
            Assert.Equal(before, PublicProperties(aggregate));
        }
    }

    private static object ADoneReportFor(Type type)
    {
        if (type == typeof(Vm)) return VmRequestEnd.Done("foreign", new VmObservation(VmState.Running, "10.0.0.9", null));
        if (type == typeof(Volume)) return VolumeRequestEnd.Done("foreign", new VolumeObservation(Guid.NewGuid(), true));
        if (type == typeof(VolumeSnapshot)) return VolumeSnapshotRequestEnd.Done("foreign");
        if (type == typeof(VmSnapshot)) return VmSnapshotRequestEnd.Done("foreign");
        if (type == typeof(Network)) return NetworkRequestEnd.Done("foreign", new NetworkObservation(NetworkStatus.Created, "10.8.0.0/24", "10.8.0.1"));
        if (type == typeof(FirewallRule)) return FirewallRuleRequestEnd.Done("foreign", FirewallRuleState.enabled);
        if (type == typeof(LoadBalancer)) return LoadBalancerRequestEnd.Done("foreign");
        if (type == typeof(VmTemplateCluster)) return VmTemplateRequestEnd.Done("foreign");
        throw new InvalidOperationException($"no report builder for {type.Name}: add one with its rollout phase");
    }

    private static Dictionary<string, object?> PublicProperties(object aggregate) =>
        aggregate.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && !typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string))
            .ToDictionary(p => p.Name, p => p.GetValue(aggregate));

    // ---------- source text: handlers lock, payloads carry the id ----------

    // A VM snapshot is reached through its VM, so every snapshot handler locks the VM's row: the create for the cap,
    // the rest so two requests on one snapshot take turns.
    [Fact]
    public void EveryVmLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Compute", "Vm"), Path.Combine("Titan.Application", "Commands", "Admin", "Vm"),
                Path.Combine("Titan.Application", "Commands", "Compute", "VmSnapshot")],
            except: ["Rename", "RequestVmSsh"], atLeast: 14, "VM");
    }

    // A volume's create makes a new row, so it has nothing to lock; a snapshot's create locks its volume for the cap.
    [Fact]
    public void EveryVolumeLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Compute", "Volume"), Path.Combine("Titan.Application", "Commands", "Compute", "VolumeSnapshot")],
            except: ["CreateVolumeCommandHandler"], atLeast: 12, "volume");
    }

    // A network's create makes a new row, so it has nothing to lock; a rule's create locks its network for the cap.
    [Fact]
    public void EveryNetworkLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Networking", "Network"), Path.Combine("Titan.Application", "Commands", "Networking", "FirewallRule")],
            except: ["CreateNetworkCommandHandler", "RenameNetworkCommandHandler"], atLeast: 9, "network");
    }

    // A load balancer's create makes a new row, so it has nothing to lock (it locks its network); a rename is Titan-side
    // metadata only (titan-ops features/load-balancer/design.md#lb-rename).
    [Fact]
    public void EveryLoadBalancerLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Compute", "LoadBalancer")],
            except: ["CreateLoadBalancerCommandHandler", "RenameLoadBalancerCommandHandler"], atLeast: 5, "load balancer");
    }

    // Every template handler is a request or a report on rows that already exist: sync-all locks each template it asks
    // a sync of, the two callbacks and the old route lock the template they report on.
    [Fact]
    public void EveryTemplateLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks([Path.Combine("Titan.Application", "Commands", "Catalog", "VmTemplate")],
            except: [], atLeast: 4, "template");
    }

    private static void AssertEveryHandlerLocks(string[] folders, string[] except, int atLeast, string resource)
    {
        var handlers = folders
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(RepoRoot.ControlPlane, f), "*CommandHandler.cs"))
            .Where(f => !except.Any(e => Path.GetFileName(f).StartsWith(e, StringComparison.Ordinal)))
            .ToList();
        Assert.True(handlers.Count >= atLeast, $"only {handlers.Count} {resource} lifecycle handlers found — the scan is blind");

        var unlocked = handlers
            .Where(f => !File.ReadAllText(f).Contains("ForUpdateAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(unlocked.Count == 0,
            $"{resource} lifecycle handlers that read without the row lock (titan-ops architecture/resource-lifecycle.md#one-key-per-request): "
            + string.Join(", ", unlocked));
    }

    [Fact]
    public void EveryVmCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var vmEnqueues = Regex.Matches(src, @"public Task EnqueueVm(Create|Delete|Stop|Start)Async\([\s\S]*?ProviderCommandRerun\.\w+, ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(4, vmEnqueues.Count);

        // A property named requestId, not any mention: stop and start also write `intentId = requestId`.
        var without = vmEnqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "EnqueueVm" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "VM command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }

    [Fact]
    public void EveryVolumeCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task EnqueueVolume(Create|Attach|Detach|Delete|SnapshotCreate|SnapshotDelete|SnapshotRestore)Async\([\s\S]*?ProviderCommandRerun\.\w+, ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(7, enqueues.Count);

        var without = enqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "EnqueueVolume" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "Volume command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }

    [Fact]
    public void EveryVmSnapshotCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task EnqueueVmSnapshot(Create|Revert|Delete)Async\([\s\S]*?ProviderCommandRerun\.\w+, ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(3, enqueues.Count);

        var without = enqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "EnqueueVmSnapshot" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "VM snapshot command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }

    // The create and the update build their payload in LoadBalancerPayload, so the scan follows each enqueue into it: an
    // inline payload must name requestId, a helper call must pass it, and the helper must write it.
    [Fact]
    public void EveryLoadBalancerCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task EnqueueLoadBalancer(Create|Update|Delete)Async\([\s\S]*?ProviderCommandRerun\.\w+, ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(3, enqueues.Count);

        Match helper = Regex.Match(src, @"private static object LoadBalancerPayload\((?<params>[^)]*)\)\s*=>\s*new\s*\{(?<body>[\s\S]*?)\};");
        Assert.True(helper.Success, "LoadBalancerPayload not found: the scan cannot follow the create and the update into it");
        int requestIdPosition = helper.Groups["params"].Value.Split(',').Select(p => p.Trim().Split(' ').Last()).ToList().IndexOf("requestId");
        Assert.True(requestIdPosition >= 0, "LoadBalancerPayload takes no requestId");
        bool helperWritesIt = Regex.IsMatch(helper.Groups["body"].Value, @"(^|[\s,{])requestId\s*,");

        var without = new List<string>();
        foreach (Match m in enqueues)
        {
            bool inline = Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]");
            Match call = Regex.Match(m.Value, @"LoadBalancerPayload\((?<args>[^;]*?)\),\s*ProviderCommandRerun");
            bool throughTheHelper = call.Success && helperWritesIt
                                    && call.Groups["args"].Value.Split(',').Select(a => a.Trim()).ElementAtOrDefault(requestIdPosition) == "requestId";
            if (!inline && !throughTheHelper) without.Add("EnqueueLoadBalancer" + m.Groups[1].Value);
        }
        Assert.True(without.Count == 0, "Load balancer command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }

    [Fact]
    public void EveryTemplateCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        Match enqueue = Regex.Match(src, @"public Task EnqueueTemplateSyncAsync\([\s\S]*?ProviderCommandRerun\.\w+, ct\);");
        Assert.True(enqueue.Success, "EnqueueTemplateSyncAsync not found: the scan is blind");

        Assert.True(Regex.IsMatch(enqueue.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]"),
            "The template sync payload carries no requestId (titan-ops architecture/resource-lifecycle.md#callback-routes)");
    }

    [Fact]
    public void EveryNetworkCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task Enqueue(NetworkCreate|NetworkDelete|FirewallRuleCreate|FirewallRuleDelete)Async\([\s\S]*?ProviderCommandRerun\.\w+, ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(4, enqueues.Count);

        var without = enqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "Enqueue" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "Network and firewall rule command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }
}
