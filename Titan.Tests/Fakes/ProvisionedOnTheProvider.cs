// Test setup for a resource the provider already holds: these replay the worker's reports under the one-call names the
// setups used before (titan-ops architecture/desired-and-current.md#desired-and-current). A volume or a snapshot has no
// state word on Incus, so its create command's Done is the whole of it.
//
// Each class lives in its aggregate's namespace so a test that uses the aggregate sees the extension without a using.

using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network
{
    /// A network's and a firewall rule's requests end by their own command's report
    /// (titan-ops architecture/resource-lifecycle.md#shape); these replay the worker's reports under the one-call names
    /// test setups used before, through the real CreateSucceeded, CreateFailed, DeleteSucceeded, DeleteFailed and MarkObserved. A
    /// replay for a request not waiting does nothing, like the report it stands for.
    public static class NetworkProvisionedOnTheProvider
    {
        /// The worker's Done for the operation the network waits on, in the one call tests used before the four methods
        /// (CreateSucceeded, DeleteSucceeded): the kind it ended, or null when it names no waiting operation.
        public static NetworkOperationKind? ReportDone(this Network network, Guid operationId, string? externalId = null,
            NetworkStatus? status = null, string? cidr = null, string? gateway = null)
        {
            NetworkOperationKind? waiting = network.Operation.Kind;
            bool ended;
            switch (waiting)
            {
                case NetworkOperationKind.Create:
                    ended = network.CreateSucceeded(operationId, externalId, status, cidr, gateway);
                    break;
                case NetworkOperationKind.Delete:
                    ended = network.DeleteSucceeded(operationId);
                    break;
                default:
                    ended = false;
                    break;
            }
            return ended ? waiting : null;
        }

        /// The worker's give-up for the operation the network waits on (CreateFailed, DeleteFailed).
        public static NetworkOperationKind? ReportFailed(this Network network, Guid operationId, string? externalId,
            NetworkStatus? status, string? cidr, string? gateway, string? reason, string? detail)
        {
            NetworkOperationKind? waiting = network.Operation.Kind;
            bool ended;
            switch (waiting)
            {
                case NetworkOperationKind.Create:
                    ended = network.CreateFailed(operationId, externalId, status, cidr, gateway, reason, detail);
                    break;
                case NetworkOperationKind.Delete:
                    ended = network.DeleteFailed(operationId, status, reason, detail);
                    break;
                default:
                    ended = false;
                    break;
            }
            return ended ? waiting : null;
        }

        /// The Done of one rule's own command through the network; a rule that is not there answers like a missing one.
        public static FirewallRuleOperationKind? ReportRuleDone(this Network network, Guid ruleId, Guid operationId,
            string? externalId, FirewallRuleState? observedState)
        {
            FirewallRule? rule = network.FirewallRules.FirstOrDefault(r => r.Id == ruleId);
            FirewallRuleOperationKind? waiting = rule?.Operation.Kind;
            bool ended = waiting == FirewallRuleOperationKind.Delete
                ? network.FirewallRuleDeleteSucceeded(ruleId, operationId)
                : network.FirewallRuleCreateSucceeded(ruleId, operationId, externalId, observedState);
            return ended ? waiting : null;
        }

        /// The give-up of one rule's own command through the network.
        public static FirewallRuleOperationKind? ReportRuleFailed(this Network network, Guid ruleId, Guid operationId,
            string? externalId, FirewallRuleState? observedState, string? reason, string? detail)
        {
            FirewallRule? rule = network.FirewallRules.FirstOrDefault(r => r.Id == ruleId);
            FirewallRuleOperationKind? waiting = rule?.Operation.Kind;
            bool ended = waiting == FirewallRuleOperationKind.Delete
                ? network.FirewallRuleDeleteFailed(ruleId, operationId, observedState, reason, detail)
                : network.FirewallRuleCreateFailed(ruleId, operationId, externalId, observedState, reason, detail);
            return ended ? waiting : null;
        }

        /// The create command's Done with the name and address, reading the network Created. True when it ended the
        /// create; with none waiting it is the reconciler's Created.
        public static bool MarkActive(this Network network, string externalId, string? cidr, string? gateway)
        {
            if (network.Operation.Kind == NetworkOperationKind.Create)
            {
                return network.ReportDone(network.Operation.PendingId!.Value, externalId, NetworkStatus.Created, cidr, gateway) != null;
            }
            network.MarkObserved(NetworkStatus.Created);
            return false;
        }

        /// The create command's Done with the name and address, before Incus shows a status.
        public static void MarkCreated(this Network network, string externalId, string? cidr, string? gateway)
        {
            if (network.Operation.Kind == NetworkOperationKind.Create)
            {
                network.ReportDone(network.Operation.PendingId!.Value, externalId, null, cidr, gateway);
            }
        }

        /// The delete command's Done.
        public static NetworkOperationKind? MarkDeleted(this Network network) =>
            network.Operation.Kind == NetworkOperationKind.Delete
                ? network.ReportDone(network.Operation.PendingId!.Value, null, null, null, null)
                : null;

        /// The create command's give-up. True when the network let go of its /64.
        public static bool MarkRequestFailed(this Network network, string? reason, string? detail, string? externalId = null)
        {
            if (network.Operation.Kind == NetworkOperationKind.Create)
            {
                bool heldAPublicIpv6 = network.HasPublicIpv6;
                NetworkOperationKind? ended = network.ReportFailed(network.Operation.PendingId!.Value, externalId, null, null, null, reason, detail);
                return ended != null && heldAPublicIpv6 && !network.HasPublicIpv6;
            }
            return false;
        }
    }

    public static class FirewallRuleProvisionedOnTheProvider
    {
        /// The Done of the rule's own command, in the one call tests used before the four methods.
        public static FirewallRuleOperationKind? ReportDone(this FirewallRule rule, Guid operationId, string? externalId,
            FirewallRuleState? observedState)
        {
            FirewallRuleOperationKind? waiting = rule.Operation.Kind;
            bool ended = waiting == FirewallRuleOperationKind.Delete
                ? rule.DeleteSucceeded(operationId)
                : rule.CreateSucceeded(operationId, externalId, observedState);
            return ended ? waiting : null;
        }

        /// The give-up of the rule's own command.
        public static FirewallRuleOperationKind? ReportFailed(this FirewallRule rule, Guid operationId, string? externalId,
            FirewallRuleState? observedState, string? reason, string? detail)
        {
            FirewallRuleOperationKind? waiting = rule.Operation.Kind;
            bool ended = waiting == FirewallRuleOperationKind.Delete
                ? rule.DeleteFailed(operationId, observedState, reason, detail)
                : rule.CreateFailed(operationId, externalId, observedState, reason, detail);
            return ended ? waiting : null;
        }

        /// The create command's Done with the rule's id, reading it enabled in the ACL. True when it ended the create.
        public static bool MarkActive(this FirewallRule rule, string externalId)
        {
            if (rule.Operation.Kind == FirewallRuleOperationKind.Create)
            {
                return rule.ReportDone(rule.Operation.PendingId!.Value, externalId, FirewallRuleState.enabled) != null;
            }
            rule.MarkObserved(FirewallRuleState.enabled);
            return false;
        }

        /// The create command's Done with the rule's id, before the rule's state is read.
        public static void MarkCreated(this FirewallRule rule, string externalId)
        {
            if (rule.Operation.Kind == FirewallRuleOperationKind.Create)
            {
                rule.ReportDone(rule.Operation.PendingId!.Value, externalId, null);
            }
        }

        /// The create command's give-up.
        public static void MarkRequestFailed(this FirewallRule rule, string? reason, string? detail, string? externalId = null)
        {
            if (rule.Operation.Kind == FirewallRuleOperationKind.Create)
            {
                rule.ReportFailed(rule.Operation.PendingId!.Value, externalId, null, reason, detail);
            }
        }

        /// The rule's own delete Done. True when its delete was waiting.
        public static bool MarkDeleted(this FirewallRule rule) =>
            rule.Operation.Kind == FirewallRuleOperationKind.Delete
            && rule.ReportDone(rule.Operation.PendingId!.Value, null, null) != null;
    }
}

namespace Titan.Domain.Aggregates.Vm
{
    /// A VM's requests end by their own command's report (titan-ops architecture/resource-lifecycle.md#shape); these replay
    /// the worker's reports under the one-call names test setups used before, through the real CreateSucceeded,
    /// CreateFailed, DeleteSucceeded and MarkObserved. A replay for a request the VM is not waiting on does nothing, like the report it
    /// stands for.
    public static class VmProvisionedOnTheProvider
    {
        /// The create command's Done with the instance name, then the reconciler's Running.
        public static void MarkRunning(this Vm vm, string externalId, string? ipAddress, string? publicIpv6Address = null)
        {
            vm.MarkCreated(externalId);
            vm.MarkObserved(VmStatus.Running, ipAddress, publicIpv6Address);
        }

        public static void MarkStopped(this Vm vm) => vm.MarkObserved(VmStatus.Stopped, null);

        /// The create command's Done, carrying only the instance name: status Unknown until an observation.
        public static void MarkCreated(this Vm vm, string externalId)
        {
            if (vm.Status == VmStatus.Creating)
            {
                vm.CreateSucceeded(vm.Operation.PendingId!.Value, externalId, null, null, null);
            }
        }

        /// The reconciler's observation, by its parts.
        public static void MarkObserved(this Vm vm, VmStatus state, string? ipAddress, string? publicIpv6Address = null) =>
            vm.MarkObserved(state, ipAddress, publicIpv6Address);

        /// The delete command's Done.
        public static bool MarkDeleted(this Vm vm) =>
            vm.Status == VmStatus.Deleting && vm.DeleteSucceeded(vm.Operation.PendingId!.Value);

        /// The create command's give-up.
        public static void MarkRequestFailed(this Vm vm, string? reason, string? detail, string? externalId = null)
        {
            if (vm.Status == VmStatus.Creating)
            {
                vm.CreateFailed(vm.Operation.PendingId!.Value, externalId, reason, detail);
            }
        }
        /// The snapshot's create command's Done with the snapshot's provider id, reported through the VM that holds it:
        /// the VM leaves Snapshotting with it, which the snapshot's own FinishOperation does not do.
        public static void MarkSnapshotCreated(this Vm vm, Guid snapshotId, string externalId)
        {
            VmSnapshot snapshot = vm.Snapshots.Single(s => s.Id == snapshotId);
            if (snapshot.Pending is { Kind: VmSnapshotOperationKind.Create } create)
            {
                vm.FinishSnapshotOperation(snapshotId, create.Id, externalId);
            }
        }
    }

    /// The same replays on one VM snapshot, for tests that build a snapshot's history directly. A report of a snapshot's
    /// command reaches it through the VM that holds it (Vm.FinishSnapshotOperation, which a create or revert's Done also
    /// uses to take the VM out of Snapshotting), so a snapshot built by TestAggregates.WithSnapshot replays through its
    /// VM; any other goes to the snapshot's own FinishOperation and FailOperation. A replay for a request not waiting
    /// does nothing, like the report it stands for.
    public static class VmSnapshotProvisionedOnTheProvider
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VmSnapshot, Vm> Owners = new();

        /// Remembers the VM that holds a snapshot a test built.
        internal static void HeldBy(this VmSnapshot snapshot, Vm vm) => Owners.AddOrUpdate(snapshot, vm);

        private static VmSnapshotOperationKind? Finish(VmSnapshot snapshot, Guid operationId, string? externalId)
        {
            if (Owners.TryGetValue(snapshot, out Vm? vm))
            {
                return vm.FinishSnapshotOperation(snapshot.Id, operationId, externalId);
            }
            return snapshot.FinishOperation(operationId, externalId);
        }

        /// The create command's Done with the snapshot's provider id. True when it ended the create.
        public static bool MarkCreated(this VmSnapshot snapshot, string externalId) =>
            snapshot.Pending is { Kind: VmSnapshotOperationKind.Create } create
            && Finish(snapshot, create.Id, externalId) != null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this VmSnapshot snapshot, string? reason, string? detail)
        {
            if (snapshot.Pending is { Kind: VmSnapshotOperationKind.Create } create)
            {
                if (Owners.TryGetValue(snapshot, out Vm? vm))
                {
                    vm.FailSnapshotOperation(snapshot.Id, create.Id, null, reason, detail);
                }
                else
                {
                    snapshot.FailOperation(create.Id, null, reason, detail);
                }
            }
        }

        /// The revert command's Done. True when a revert was waiting.
        public static bool MarkReverted(this VmSnapshot snapshot) =>
            snapshot.Pending is { Kind: VmSnapshotOperationKind.Revert } revert
            && Finish(snapshot, revert.Id, null) != null;

        /// The delete command's Done. True when a delete was waiting. The VM's delete ending its snapshots is
        /// EndWithItsVm.
        public static bool MarkDeleted(this VmSnapshot snapshot) =>
            snapshot.Pending is { Kind: VmSnapshotOperationKind.Delete } delete
            && Finish(snapshot, delete.Id, null) != null;
    }
}

namespace Titan.Domain.Aggregates.Volume
{
    /// A volume's and a volume snapshot's requests end by their own command's report
    /// (titan-ops architecture/resource-lifecycle.md#shape); these replay the worker's reports under the one-call names
    /// test setups used before, through the real FinishOperation and FailOperation. A replay for a request not waiting
    /// does nothing.
    public static class VolumeProvisionedOnTheProvider
    {
        /// The create (or restore) command's Done with the volume name.
        public static void MarkCreated(this Volume volume, string externalId)
        {
            if (volume.Pending is { Kind: VolumeOperationKind.Create } create)
            {
                volume.FinishOperation(create.Id, externalId, false, null, false);
            }
        }

        /// The attach command's Done, reading the volume used by that VM.
        public static void MarkAttached(this Volume volume, Guid vmId)
        {
            if (volume.Pending is { Kind: VolumeOperationKind.Attach } attach)
            {
                volume.FinishOperation(attach.Id, null, true, vmId, false);
            }
        }

        /// The delete command's Done.
        public static VolumeOperationKind? MarkDeleted(this Volume volume) =>
            volume.Pending is { Kind: VolumeOperationKind.Delete } delete
                ? volume.FinishOperation(delete.Id, null, false, null, false)
                : null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this Volume volume, string? reason, string? detail, string? externalId = null)
        {
            if (volume.Pending is { Kind: VolumeOperationKind.Create } create)
            {
                volume.FailOperation(create.Id, externalId, false, null, false, reason, detail);
            }
        }

        /// The snapshot's create command's Done with the snapshot's provider id.
        public static void MarkSnapshotCreated(this Volume volume, Guid snapshotId, string externalId)
        {
            VolumeSnapshot snapshot = volume.Snapshots.Single(s => s.Id == snapshotId);
            if (snapshot.Pending is { Kind: VolumeSnapshotOperationKind.Create } create)
            {
                volume.FinishSnapshotOperation(snapshotId, create.Id, externalId);
            }
        }
    }

    /// The same replays on one snapshot, for tests that build a snapshot's history directly.
    public static class VolumeSnapshotProvisionedOnTheProvider
    {
        /// The create command's Done with the snapshot's provider id.
        public static void MarkCreated(this VolumeSnapshot snapshot, string externalId)
        {
            if (snapshot.Pending is { Kind: VolumeSnapshotOperationKind.Create } create)
            {
                snapshot.FinishOperation(create.Id, externalId);
            }
        }

        /// The create command's give-up.
        public static void MarkRequestFailed(this VolumeSnapshot snapshot, string? reason, string? detail)
        {
            if (snapshot.Pending is { Kind: VolumeSnapshotOperationKind.Create } create)
            {
                snapshot.FailOperation(create.Id, null, reason, detail);
            }
        }

        /// Incus no longer has it after Titan's delete: its own delete's Done, or its volume's.
        public static void MarkDeleted(this VolumeSnapshot snapshot) => snapshot.EndWithItsVolume();
    }
}

namespace Titan.Domain.Aggregates.LoadBalancer
{
    /// A load balancer's request ends by its own command's report (titan-ops architecture/resource-lifecycle.md#shape);
    /// these replay the worker's reports under the one-call names test setups used before, through the real
    /// FinishOperation and FailOperation. A replay for a request not waiting does nothing, like the report it stands for.
    public static class LoadBalancerProvisionedOnTheProvider
    {
        /// The create command's Done with the VIP. True when it ended the create.
        public static bool MarkCreated(this LoadBalancer loadBalancer, string externalId) =>
            loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Create } create
            && loadBalancer.FinishOperation(create.Id, externalId) != null;

        /// The update command's Done for its request. True when that update was the one waiting.
        public static bool MarkUpdated(this LoadBalancer loadBalancer, Guid requestId) =>
            loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Update } update
            && update.Id == requestId
            && loadBalancer.FinishOperation(requestId, null) != null;

        /// The delete command's Done. True when a delete was waiting.
        public static bool MarkDeleted(this LoadBalancer loadBalancer) =>
            loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Delete } delete
            && loadBalancer.FinishOperation(delete.Id, null) != null;

        /// The create command's give-up, before Incus had the VIP.
        public static void MarkRequestFailed(this LoadBalancer loadBalancer, string? reason, string? detail)
        {
            if (loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Create } create)
            {
                loadBalancer.FailOperation(create.Id, null, reason, detail);
            }
        }
    }
}

namespace Titan.Domain.Aggregates.VmTemplate
{
    /// A template's sync in a cluster ends by its own command's report (titan-ops architecture/resource-lifecycle.md#shape);
    /// these replay the worker's reports under the one-call names test setups used before, through the real
    /// FinishSyncOperation and FailSyncOperation. A replay for a sync not waiting ends nothing.
    public static class VmTemplateProvisionedOnTheProvider
    {
        /// The sync command's Done with the alias. True when it ended a waiting sync; with none waiting there is no
        /// request to end, so nothing changes.
        public static bool MarkCreatedIn(this VmTemplate template, string clusterId, string externalTemplateId)
        {
            Values.PendingOperation<VmTemplateOperationKind>? waiting = template.Clusters.FirstOrDefault(c => c.ClusterId == clusterId)?.Pending;
            return waiting != null && template.FinishSyncOperation(clusterId, waiting.Id, externalTemplateId) != null;
        }

        /// The sync command's give-up, before Incus had the alias. True when a sync was waiting.
        public static bool MarkRequestFailedIn(this VmTemplate template, string clusterId, string? reason, string? detail)
        {
            Values.PendingOperation<VmTemplateOperationKind>? waiting = template.Clusters.FirstOrDefault(c => c.ClusterId == clusterId)?.Pending;
            return waiting != null
                   && template.FailSyncOperation(clusterId, waiting.Id, null, reason, detail) != null;
        }
    }
}
