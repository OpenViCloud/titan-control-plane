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

// titan-ops architecture/resource-lifecycle.md#shape: every aggregate on the request shape has the same five methods (the
// VM, the network, the firewall rule, the VM snapshot, the volume, the volume snapshot and the template in a cluster have
// one report method per move instead of FinishOperation and FailOperation), its Mark* return nothing, a report with a foreign id changes nothing,
// every lifecycle handler locks the row, and every command payload carries the request id. Reflection and a factory for
// the first three; source text for the last two.
//
// What this is SILENT about: a lock taken by another name, a lock on another row (create locks its network), a payload built outside ProviderCommandQueue, and the
// semantics of the report methods beyond "a foreign id is ignored" (VmStatusTests, VmRequestLifecycleTests, VolumeRequestLifecycleTests,
// NetworkRequestLifecycleTests, VmSnapshotRequestLifecycleTests, LoadBalancerRequestLifecycleTests and
// VmTemplateSyncRequestLifecycleTests hold those). A child entity (VolumeSnapshot, FirewallRule, VmSnapshot,
// VmTemplateCluster) is on the shape with internal methods, reached through its root. Vm is on the shape without a
// Pending property: its one status says what the waiting request asks, and it keeps only that request's id
// (titan-ops features/vm/design.md#vm-status), so the scan cannot find it and it is named here instead. Vm, Network,
// FirewallRule, VmSnapshot, Volume, VolumeSnapshot and VmTemplateCluster keep the waiting operation in an Operation of their own (titan-ops
// features/network/design.md#network-operation, features/vm/design.md#vm-snapshot, features/volume/design.md#volume-state-is-existence,
// features/catalog/design.md#template-state-is-existence) and are named here for the same reason.
public class RequestLifecycleShapeTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // On the shape without a Pending property, each kept by name.
    private static readonly Type[] OnTheShapeWithoutPending =
        [typeof(Vm), typeof(Network), typeof(FirewallRule), typeof(VmSnapshot), typeof(Volume), typeof(VolumeSnapshot), typeof(VmTemplateCluster)];

    // Aggregates with a Pending property (titan-ops architecture/resource-lifecycle.md#rollout).
    private static List<Type> WithAPendingProperty() =>
        typeof(Vm).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && t.Namespace is not null && t.Namespace.StartsWith("Titan.Domain.Aggregates.", StringComparison.Ordinal)
                        && t.GetProperty("Pending", BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToList();

    // Aggregates on the new shape: the ones with a Pending property, and the ones named without it.
    private static List<Type> OnTheRequestShape() => [.. WithAPendingProperty(), .. OnTheShapeWithoutPending];

    // Each rollout phase adds its aggregate here, with the factory that builds one waiting on a request.
    private static readonly Dictionary<Type, Func<object>> WaitingOnARequest = new()
    {
        [typeof(Vm)] = () => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20),
        [typeof(Volume)] = () => Volume.Create(TestTenant.WithRoomToSpare().Id, "shape", "cluster-a", "std", 10),
        [typeof(VolumeSnapshot)] = () =>
        {
            Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, "shape", "cluster-a", "std", 10);
            volume.CreateSucceeded(volume.Operation.PendingId!.Value, "vol-shape", false, null, false);
            return volume.WithSnapshot("shape");
        },
        [typeof(VmSnapshot)] = () =>
        {
            Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20);
            vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-shape", VmStatus.Running, "10.0.0.9", null);
            return vm.WithSnapshot("shape");
        },
        [typeof(Network)] = () => ANetwork(),
        [typeof(LoadBalancer)] = () =>
        {
            const string subnet = "2001:db8:0:9::/64";
            Guid tenantId = TestTenant.WithRoomToSpare().Id;
            Guid networkId = Guid.NewGuid();
            Vm backend = Vm.Create(tenantId, networkId, "shape", "m", "tpl", "cluster-a", 20);
            backend.CreateSucceeded(backend.Operation.PendingId!.Value, "vm-shape", VmStatus.Running, "10.0.0.9", null);
            LoadBalancer lb = LoadBalancer.Create(tenantId, "shape", networkId, "net-shape", subnet, "cluster-a",
                LoadBalancerVip.For(subnet, 1), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [backend]);
            lb.FinishOperation(lb.Pending!.Id, lb.Vip);
            lb.Update([new LoadBalancerListenerSpec(LoadBalancerProtocol.UDP, 53, 53)], [backend]);
            return lb;
        },
        [typeof(FirewallRule)] = () =>
        {
            Network network = ANetwork();
            network.ReportDone(network.Operation.PendingId!.Value, "net-shape", NetworkStatus.Created, "10.9.0.0/24", "10.9.0.1");
            return network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8"));
        },
        [typeof(VmTemplateCluster)] = () =>
        {
            VmTemplate template = VmTemplate.Create("shape", null, "ubuntu/22.04", 10, "m");
            template.SyncIn("cluster-a");
            return template.Clusters.Single();
        },
    };

    private static Network ANetwork() =>
        Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("shape"), "cluster-a", null, wantsPublicIpv6: false);

    // The intent methods are plain verbs (titan-ops architecture/resource-lifecycle.md#shape).
    private static readonly HashSet<string> IntentVerbs = ["Start", "Stop", "Delete", "Update", "Sync", "SyncIn"];

    [Fact]
    public void EveryAggregateOnTheShape_HasTheFiveMethods_AndIsKnownHere()
    {
        List<Type> shaped = OnTheRequestShape();
        Assert.NotEmpty(shaped);
        Assert.Equal(shaped.OrderBy(t => t.Name), WaitingOnARequest.Keys.OrderBy(t => t.Name));

        var missing = new List<string>();
        foreach (Type type in shaped)
        {
            string[] names = type.GetMethods(Declared).Select(m => m.Name).Distinct().ToArray();
            foreach (string required in RequiredReportsOf(type))
            {
                if (!names.Contains(required)) missing.Add($"{type.Name}.{required}");
            }
            if (!names.Any(n => IntentVerbs.Contains(n))) missing.Add($"{type.Name}.<intent verb>");
        }
        Assert.True(missing.Count == 0, "Aggregates on the request shape without the five methods: " + string.Join(", ", missing));
    }

    // A type named without Pending must still be invisible to the scan (or it would be counted twice) and still keep the
    // waiting operation's id, the one thing a report is compared with, in its Operation.
    [Fact]
    public void ATypeNamedWithoutPending_HasNoPendingProperty_AndKeepsTheWaitingOperationsId()
    {
        List<Type> scanned = WithAPendingProperty();
        foreach (Type type in OnTheShapeWithoutPending)
        {
            Assert.DoesNotContain(type, scanned);
            PropertyInfo? operation = type.GetProperty("Operation", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(operation != null, $"{type.Name} keeps no Operation");
            PropertyInfo? id = operation.PropertyType.GetProperty("PendingId", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(id != null, $"{type.Name}.Operation keeps no PendingId");
            Assert.Equal(typeof(Guid?), id.PropertyType);
        }
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

    // The VM has one report method per move (titan-ops features/vm/design.md#vm-status); the network, the firewall rule, the
    // VM snapshot, the volume and the volume snapshot have one Succeeded and one Failed move per operation (NetworkReportMoves,
    // VmSnapshotReportMoves, VolumeReportMoves, VolumeSnapshotReportMoves and VmTemplateReportMoves below); the other type
    // (the load balancer) ends a request by FinishOperation and FailOperation.
    private static readonly string[] VmReportMoves =
    [
        "CreateSucceeded", "CreateFailed", "StartSucceeded", "StartFailed",
        "StopSucceeded", "StopFailed", "DeleteSucceeded", "DeleteFailed",
    ];

    // Network and FirewallRule end an operation by its kind too (titan-ops features/network/design.md#network-operation).
    private static readonly string[] NetworkReportMoves = ["CreateSucceeded", "CreateFailed", "DeleteSucceeded", "DeleteFailed"];

    // A VM snapshot ends its create, revert or delete by the status it waits in (titan-ops features/vm/design.md#vm-snapshot-reports).
    private static readonly string[] VmSnapshotReportMoves =
    [
        "CreateSucceeded", "CreateFailed", "RevertSucceeded", "RevertFailed", "DeleteSucceeded", "DeleteFailed",
    ];

    // A volume ends its create, attach, detach or delete by the status it waits in (titan-ops features/volume/design.md#volume-reports).
    private static readonly string[] VolumeReportMoves =
    [
        "CreateSucceeded", "CreateFailed", "AttachSucceeded", "AttachFailed", "DetachSucceeded", "DetachFailed",
        "DeleteSucceeded", "DeleteFailed",
    ];

    // A volume snapshot ends its create or delete by the status it waits in (titan-ops features/volume/design.md#volume-snapshot).
    private static readonly string[] VolumeSnapshotReportMoves = ["CreateSucceeded", "CreateFailed", "DeleteSucceeded", "DeleteFailed"];

    // A template in a cluster ends its one operation, a sync, by the status it waits in (titan-ops features/catalog/design.md#template-reports).
    private static readonly string[] VmTemplateReportMoves = ["SyncSucceeded", "SyncFailed"];

    private static IEnumerable<string> RequiredReportsOf(Type type)
    {
        if (type == typeof(Vm))
        {
            return [.. VmReportMoves, "MarkObserved", "MarkNotFound"];
        }
        if (type == typeof(Network) || type == typeof(FirewallRule))
        {
            return [.. NetworkReportMoves, "MarkObserved", "MarkNotFound"];
        }
        if (type == typeof(VmSnapshot))
        {
            return [.. VmSnapshotReportMoves, "MarkObserved", "MarkNotFound"];
        }
        if (type == typeof(Volume))
        {
            return [.. VolumeReportMoves, "MarkObserved", "MarkNotFound"];
        }
        if (type == typeof(VolumeSnapshot))
        {
            return [.. VolumeSnapshotReportMoves, "MarkObserved", "MarkNotFound"];
        }
        if (type == typeof(VmTemplateCluster))
        {
            return [.. VmTemplateReportMoves, "MarkObserved", "MarkNotFound"];
        }
        return ["FinishOperation", "FailOperation", "MarkObserved", "MarkNotFound"];
    }

    // Each VM move run for real on a VM waiting in that move's status: a report naming another operation returns false
    // and leaves every public property as it was.
    [Fact]
    public void EveryVmMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty()
    {
        foreach (string move in VmReportMoves)
        {
            Vm vm = VmWaitingFor(move);
            MethodInfo ending = typeof(Vm).GetMethod(move, BindingFlags.Public | BindingFlags.Instance)!;
            object?[] arguments = VmReportArguments(ending, Guid.NewGuid());
            Dictionary<string, object?> before = PublicProperties(vm);

            object? result = ending.Invoke(vm, arguments);

            Assert.Equal(false, result);
            Assert.Equal(before, PublicProperties(vm));
        }
    }

    // Each move of a network and of a rule run for real on one waiting on its create: a report naming another operation
    // returns false and leaves every public property as it was.
    [Fact]
    public void EveryNetworkAndRuleMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty()
    {
        foreach (Type type in new[] { typeof(Network), typeof(FirewallRule) })
        {
            foreach (string move in NetworkReportMoves)
            {
                object aggregate = WaitingOnARequest[type]();
                MethodInfo ending = type.GetMethod(move, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                object?[] arguments = [Guid.NewGuid(), .. ending.GetParameters().Skip(1).Select(ForeignArgument)];
                Dictionary<string, object?> before = PublicProperties(aggregate);

                object? result = ending.Invoke(aggregate, arguments);

                Assert.Equal(false, result);
                Assert.Equal(before, PublicProperties(aggregate));
            }
        }
    }

    // Each move of a VM snapshot run for real on a snapshot waiting in that move's status, so the false comes from the id
    // alone: a report naming another operation returns false and leaves every public property as it was.
    [Fact]
    public void EveryVmSnapshotMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty()
    {
        foreach (string move in VmSnapshotReportMoves)
        {
            VmSnapshot snapshot = VmSnapshotWaitingFor(move);
            MethodInfo ending = typeof(VmSnapshot).GetMethod(move, BindingFlags.NonPublic | BindingFlags.Instance)!;
            object?[] arguments = [Guid.NewGuid(), .. ending.GetParameters().Skip(1).Select(ForeignArgument)];
            Dictionary<string, object?> before = PublicProperties(snapshot);

            object? result = ending.Invoke(snapshot, arguments);

            Assert.Equal(false, result);
            Assert.Equal(before, PublicProperties(snapshot));
        }
    }

    private static VmSnapshot VmSnapshotWaitingFor(string move)
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-shape", VmStatus.Running, "10.0.0.9", null);
        VmSnapshot snapshot = vm.WithSnapshot("shape");
        if (move.StartsWith("Create", StringComparison.Ordinal))
        {
            return snapshot;
        }
        snapshot.MarkCreated("vm-shape/s1");
        if (move.StartsWith("Revert", StringComparison.Ordinal))
        {
            vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
        }
        else
        {
            vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
        }
        return snapshot;
    }

    // Each volume move run for real on a volume waiting in that move's status: a report naming another operation returns
    // false and leaves every public property as it was.
    [Fact]
    public void EveryVolumeMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty()
    {
        foreach (string move in VolumeReportMoves)
        {
            Volume volume = VolumeWaitingFor(move);
            MethodInfo ending = typeof(Volume).GetMethod(move, BindingFlags.Public | BindingFlags.Instance)!;
            object?[] arguments = [Guid.NewGuid(), .. ending.GetParameters().Skip(1).Select(StorageForeignArgument)];
            Dictionary<string, object?> before = PublicProperties(volume);

            object? result = ending.Invoke(volume, arguments);

            Assert.Equal(false, result);
            Assert.Equal(before, PublicProperties(volume));
        }
    }

    // The same for a volume snapshot, whose moves are internal to it and reached through its volume.
    [Fact]
    public void EveryVolumeSnapshotMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty()
    {
        foreach (string move in VolumeSnapshotReportMoves)
        {
            VolumeSnapshot snapshot = VolumeSnapshotWaitingFor(move);
            MethodInfo ending = typeof(VolumeSnapshot).GetMethod(move, BindingFlags.NonPublic | BindingFlags.Instance)!;
            object?[] arguments = [Guid.NewGuid(), .. ending.GetParameters().Skip(1).Select(StorageForeignArgument)];
            Dictionary<string, object?> before = PublicProperties(snapshot);

            object? result = ending.Invoke(snapshot, arguments);

            Assert.Equal(false, result);
            Assert.Equal(before, PublicProperties(snapshot));
        }
    }

    // The same for a template in a cluster: its moves are internal to it and reached through its template.
    [Fact]
    public void EveryVmTemplateClusterMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty()
    {
        foreach (string move in VmTemplateReportMoves)
        {
            VmTemplateCluster cluster = (VmTemplateCluster)WaitingOnARequest[typeof(VmTemplateCluster)]();
            MethodInfo ending = typeof(VmTemplateCluster).GetMethod(move, BindingFlags.NonPublic | BindingFlags.Instance)!;
            object?[] arguments = [Guid.NewGuid(), .. ending.GetParameters().Skip(1).Select(ForeignArgument)];
            Dictionary<string, object?> before = PublicProperties(cluster);

            object? result = ending.Invoke(cluster, arguments);

            Assert.Equal(false, result);
            Assert.Equal(before, PublicProperties(cluster));
        }
    }

    private static Volume VolumeWaitingFor(string move)
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, "shape", "cluster-a", "std", 10);
        if (move.StartsWith("Create", StringComparison.Ordinal))
        {
            return volume;
        }
        volume.MarkCreated("vol-shape");
        if (move.StartsWith("Delete", StringComparison.Ordinal))
        {
            volume.Delete();
            return volume;
        }
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-shape", VmStatus.Running, "10.0.0.9", null);
        volume.Attach(vm);
        if (move.StartsWith("Detach", StringComparison.Ordinal))
        {
            volume.MarkAttached(vm.Id);
            volume.Detach();
        }
        return volume;
    }

    private static VolumeSnapshot VolumeSnapshotWaitingFor(string move)
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, "shape", "cluster-a", "std", 10);
        volume.MarkCreated("vol-shape");
        VolumeSnapshot snapshot = volume.WithSnapshot("shape");
        if (move.StartsWith("Create", StringComparison.Ordinal))
        {
            return snapshot;
        }
        snapshot.MarkCreated("vol-shape/s1");
        volume.DeleteSnapshot(snapshot.Id);
        return snapshot;
    }

    // A volume or snapshot report after the operation id: a plausible value for each other parameter of the move.
    private static object? StorageForeignArgument(ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(bool)) return true;
        if (parameter.ParameterType == typeof(Guid?)) return Guid.NewGuid();
        if (parameter.Name == "externalId") return "foreign";
        return "late";
    }

    private static object? ForeignArgument(ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(NetworkStatus?)) return NetworkStatus.Created;
        if (parameter.ParameterType == typeof(FirewallRuleStatus?)) return FirewallRuleStatus.enabled;
        if (parameter.Name == "externalId") return "foreign";
        return "late";
    }

    private static Vm VmWaitingFor(string move)
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20);
        if (move.StartsWith("Create", StringComparison.Ordinal))
        {
            return vm;
        }
        bool fromStopped = move.StartsWith("Start", StringComparison.Ordinal);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-shape", fromStopped ? VmStatus.Stopped : VmStatus.Running, null, null);
        if (move.StartsWith("Start", StringComparison.Ordinal)) vm.Start(Guid.NewGuid());
        else if (move.StartsWith("Stop", StringComparison.Ordinal)) vm.Stop(Guid.NewGuid());
        else vm.Delete(Guid.NewGuid());
        return vm;
    }

    // The operation id first, then a plausible value for every other parameter of the move.
    private static object?[] VmReportArguments(MethodInfo move, Guid operationId)
    {
        var arguments = new List<object?> { operationId };
        foreach (ParameterInfo parameter in move.GetParameters().Skip(1))
        {
            if (parameter.ParameterType == typeof(VmStatus?)) arguments.Add(VmStatus.Running);
            else if (parameter.Name == "externalId") arguments.Add("foreign");
            else arguments.Add("late");
        }
        return [.. arguments];
    }

    [Fact] // run for real, through each type's factory: a report naming another operation leaves every public property as it was
    public void FinishOrFail_WithAForeignId_ReturnsNull_AndChangesNoPublicProperty()
    {
        foreach ((Type type, Func<object> build) in WaitingOnARequest)
        {
            if (type == typeof(Vm))
            {
                continue; // EveryVmMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty
            }
            if (type == typeof(Network) || type == typeof(FirewallRule))
            {
                continue; // EveryNetworkAndRuleMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty
            }
            if (type == typeof(VmSnapshot))
            {
                continue; // EveryVmSnapshotMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty
            }
            if (type == typeof(Volume) || type == typeof(VolumeSnapshot))
            {
                continue; // EveryVolumeMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty and the snapshot's
            }
            if (type == typeof(VmTemplateCluster))
            {
                continue; // EveryVmTemplateClusterMove_WithAForeignId_ReturnsFalse_AndChangesNoPublicProperty
            }
            foreach (string method in new[] { "FinishOperation", "FailOperation" })
            {
                object aggregate = build();
                MethodInfo ending = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                object?[] arguments = [Guid.NewGuid(), .. WhatTheWorkerSawFor(type, method)];
                Dictionary<string, object?> before = PublicProperties(aggregate);

                object? result = ending.Invoke(aggregate, arguments);

                Assert.Null(result);
                Assert.Equal(before, PublicProperties(aggregate));
            }
        }
    }

    // Everything a report brings after the operation id: what the worker saw, and for a give-up the reason and detail.
    private static object?[] WhatTheWorkerSawFor(Type type, string method)
    {
        object?[] seen;
        if (type == typeof(Network)) seen = ["foreign", NetworkStatus.Created, "10.8.0.0/24", "10.8.0.1"];
        else if (type == typeof(FirewallRule)) seen = ["foreign", FirewallRuleStatus.enabled];
        else if (type == typeof(LoadBalancer)) seen = ["foreign"];
        else throw new InvalidOperationException($"no report for {type.Name}: add one with its rollout phase");
        if (method == "FinishOperation")
        {
            return seen;
        }
        return [.. seen, "late", "late detail"];
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
            except: ["Rename", "RequestVmSsh"], atLeast: 12, "VM");
    }

    // A volume's create makes a new row, so it has nothing to lock; a snapshot's create locks its volume for the cap.
    [Fact]
    public void EveryVolumeLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Compute", "Volume"), Path.Combine("Titan.Application", "Commands", "Compute", "VolumeSnapshot")],
            except: ["CreateVolumeCommandHandler"], atLeast: 10, "volume");
    }

    // A network's create makes a new row, so it has nothing to lock; a rule's create locks its network for the cap.
    [Fact]
    public void EveryNetworkLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Networking", "Network"), Path.Combine("Titan.Application", "Commands", "Networking", "FirewallRule")],
            except: ["CreateNetworkCommandHandler", "RenameNetworkCommandHandler"], atLeast: 7, "network");
    }

    // A load balancer's create makes a new row, so it has nothing to lock (it locks its network); a rename is Titan-side
    // metadata only (titan-ops features/load-balancer/design.md#lb-rename).
    [Fact]
    public void EveryLoadBalancerLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks(
            [Path.Combine("Titan.Application", "Commands", "Compute", "LoadBalancer")],
            except: ["CreateLoadBalancerCommandHandler", "RenameLoadBalancerCommandHandler"], atLeast: 4, "load balancer");
    }

    // Every template handler is a request or a report on rows that already exist: sync-all locks each template it asks
    // a sync of, the two callbacks lock the template they report on.
    [Fact]
    public void EveryTemplateLifecycleHandler_LocksTheRow()
    {
        AssertEveryHandlerLocks([Path.Combine("Titan.Application", "Commands", "Catalog", "VmTemplate")],
            except: [], atLeast: 3, "template");
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
        var vmEnqueues = Regex.Matches(src, @"public Task EnqueueVm(Create|Delete|Stop|Start)Async\([\s\S]*?,\s*ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(4, vmEnqueues.Count);

        // A property named requestId, not any mention of the word.
        var without = vmEnqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "EnqueueVm" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "VM command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }

    [Fact]
    public void EveryVolumeCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task EnqueueVolume(Create|Attach|Detach|Delete|SnapshotCreate|SnapshotDelete|SnapshotRestore)Async\([\s\S]*?,\s*ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(7, enqueues.Count);

        var without = enqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "EnqueueVolume" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "Volume command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }

    [Fact]
    public void EveryVmSnapshotCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task EnqueueVmSnapshot(Create|Revert|Delete)Async\([\s\S]*?,\s*ct\);")
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
        var enqueues = Regex.Matches(src, @"public Task EnqueueLoadBalancer(Create|Update|Delete)Async\([\s\S]*?,\s*ct\);")
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
            Match call = Regex.Match(m.Value, @"LoadBalancerPayload\((?<args>[^;]*?)\),\s*ct\)");
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
        Match enqueue = Regex.Match(src, @"public Task EnqueueTemplateSyncAsync\([\s\S]*?,\s*ct\);");
        Assert.True(enqueue.Success, "EnqueueTemplateSyncAsync not found: the scan is blind");

        Assert.True(Regex.IsMatch(enqueue.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]"),
            "The template sync payload carries no requestId (titan-ops architecture/resource-lifecycle.md#callback-routes)");
    }

    [Fact]
    public void EveryNetworkCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var enqueues = Regex.Matches(src, @"public Task Enqueue(NetworkCreate|NetworkDelete|FirewallRuleCreate|FirewallRuleDelete)Async\([\s\S]*?,\s*ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(4, enqueues.Count);

        var without = enqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "Enqueue" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "Network and firewall rule command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }
}
