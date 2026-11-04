// Test setup for a resource the provider already holds: these replay the worker's reports under the one-call names the
// setups used before (titan-ops architecture/desired-and-current.md#desired-and-current). A volume or a snapshot has no
// state word on Incus, so its create command's Done is the whole of it.
//
// Each class lives in its aggregate's namespace so a test that uses the aggregate sees the extension without a using.

namespace Titan.Domain.Aggregates.Network
{
    /// A network's and a firewall rule's requests end by their own command's report
    /// (titan-ops architecture/resource-lifecycle.md#shape); these replay the worker's reports under the one-call names
    /// test setups used before, through the real EndOperation and MarkObserved. A replay for a request not waiting does
    /// nothing, like the report it stands for.
    public static class NetworkProvisionedOnTheProvider
    {
        /// The create command's Done with the name and address, reading the network Created. True when it ended the
        /// create; with none waiting it is the reconciler's Created.
        public static bool MarkActive(this Network network, string externalId, string? cidr, string? gateway)
        {
            if (network.Pending is { Kind: NetworkOperationKind.Create } create)
            {
                return network.EndOperation(create.Id,
                    NetworkOperationEnd.Done(externalId, new NetworkObservation(NetworkStatus.Created, cidr, gateway))) != null;
            }
            network.MarkObserved(NetworkStatus.Created);
            return false;
        }

        /// The create command's Done with the name and address, before Incus shows a status.
        public static void MarkCreated(this Network network, string externalId, string? cidr, string? gateway)
        {
            if (network.Pending is { Kind: NetworkOperationKind.Create } create)
            {
                network.EndOperation(create.Id, NetworkOperationEnd.Done(externalId, new NetworkObservation(null, cidr, gateway)));
            }
        }

        /// The delete command's Done.
        public static NetworkOperationEnded? MarkDeleted(this Network network) =>
            network.Pending is { Kind: NetworkOperationKind.Delete } delete
                ? network.EndOperation(delete.Id, NetworkOperationEnd.Done(null, null))
                : null;

        /// The create command's give-up. True when the network let go of its /64.
        public static bool MarkRequestFailed(this Network network, string? reason, string? detail, string? externalId = null)
        {
            if (network.Pending is { Kind: NetworkOperationKind.Create } create)
            {
                NetworkOperationEnded? ended = network.EndOperation(create.Id,
                    NetworkOperationEnd.Failed(externalId, null, reason, detail));
                return ended is { LetGoOfPublicIpv6: true };
            }
            return false;
        }
    }

    public static class FirewallRuleProvisionedOnTheProvider
    {
        /// The create command's Done with the rule's id, reading it enabled in the ACL. True when it ended the create.
        public static bool MarkActive(this FirewallRule rule, string externalId)
        {
            if (rule.Pending is { Kind: FirewallRuleOperationKind.Create } create)
            {
                return rule.EndOperation(create.Id, FirewallRuleOperationEnd.Done(externalId, FirewallRuleState.enabled)) != null;
            }
            rule.MarkObserved(FirewallRuleState.enabled);
            return false;
        }

        /// The create command's Done with the rule's id, before the rule's state is read.
        public static void MarkCreated(this FirewallRule rule, string externalId)
        {
            if (rule.Pending is { Kind: FirewallRuleOperationKind.Create } create)
            {
                rule.EndOperation(create.Id, FirewallRuleOperationEnd.Done(externalId, null));
            }
        }

        /// The create command's give-up.
        public static void MarkRequestFailed(this FirewallRule rule, string? reason, string? detail, string? externalId = null)
        {
            if (rule.Pending is { Kind: FirewallRuleOperationKind.Create } create)
            {
                rule.EndOperation(create.Id, FirewallRuleOperationEnd.Failed(externalId, reason, detail));
            }
        }

        /// The rule's own delete Done. True when its delete was waiting.
        public static bool MarkDeleted(this FirewallRule rule) =>
            rule.Pending is { Kind: FirewallRuleOperationKind.Delete } delete
            && rule.EndOperation(delete.Id, FirewallRuleOperationEnd.Done(null, null)) != null;
    }
}

namespace Titan.Domain.Aggregates.Vm
{
    /// A VM's requests end by their own command's report (titan-ops architecture/resource-lifecycle.md#shape); these replay
    /// the worker's reports under the one-call names test setups used before, through the real EndOperation and
    /// MarkObserved. A replay for a request the VM is not waiting on does nothing, like the report it stands for.
    public static class VmProvisionedOnTheProvider
    {
        /// The create command's Done with the instance name, then the reconciler's Running.
        public static void MarkRunning(this Vm vm, string externalId, string? ipAddress, string? publicIpv6Address = null)
        {
            vm.MarkCreated(externalId);
            vm.MarkObserved(VmState.Running, ipAddress, publicIpv6Address);
        }

        public static void MarkStopped(this Vm vm) => vm.MarkObserved(VmState.Stopped, null);

        /// The create command's Done, carrying only the instance name: existence, no status yet.
        public static void MarkCreated(this Vm vm, string externalId)
        {
            if (vm.Pending is { Kind: VmOperationKind.Create } create)
            {
                vm.EndOperation(create.Id, VmOperationEnd.Done(externalId, null));
            }
        }

        /// The reconciler's observation, by its parts.
        public static void MarkObserved(this Vm vm, VmState state, string? ipAddress, string? publicIpv6Address = null) =>
            vm.MarkObserved(new VmObservation(state, ipAddress, publicIpv6Address));

        /// The delete command's Done.
        public static VmOperationEnded? MarkDeleted(this Vm vm) =>
            vm.Pending is { Kind: VmOperationKind.Delete } delete
                ? vm.EndOperation(delete.Id, VmOperationEnd.Done(null, null))
                : null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this Vm vm, string? reason, string? detail, string? externalId = null)
        {
            if (vm.Pending is { Kind: VmOperationKind.Create } create)
            {
                vm.EndOperation(create.Id, VmOperationEnd.Failed(externalId, null, reason, detail));
            }
        }
        /// The snapshot's create command's Done with the snapshot's provider id.
        public static void MarkSnapshotCreated(this Vm vm, Guid snapshotId, string externalId)
        {
            VmSnapshot snapshot = vm.Snapshots.Single(s => s.Id == snapshotId);
            if (snapshot.Pending is { Kind: VmSnapshotOperationKind.Create } create)
            {
                vm.EndSnapshotOperation(snapshotId, create.Id, VmSnapshotOperationEnd.Done(externalId));
            }
        }
    }

    /// The same replays on one VM snapshot, for tests that build a snapshot's history directly, through the real
    /// EndOperation. A replay for a request not waiting does nothing, like the report it stands for.
    public static class VmSnapshotProvisionedOnTheProvider
    {
        /// The create command's Done with the snapshot's provider id. True when it ended the create.
        public static bool MarkCreated(this VmSnapshot snapshot, string externalId) =>
            snapshot.Pending is { Kind: VmSnapshotOperationKind.Create } create
            && snapshot.EndOperation(create.Id, VmSnapshotOperationEnd.Done(externalId)) != null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this VmSnapshot snapshot, string? reason, string? detail)
        {
            if (snapshot.Pending is { Kind: VmSnapshotOperationKind.Create } create)
            {
                snapshot.EndOperation(create.Id, VmSnapshotOperationEnd.Failed(null, reason, detail));
            }
        }

        /// The revert command's Done. True when a revert was waiting.
        public static bool MarkReverted(this VmSnapshot snapshot) =>
            snapshot.Pending is { Kind: VmSnapshotOperationKind.Revert } revert
            && snapshot.EndOperation(revert.Id, VmSnapshotOperationEnd.Done(null)) != null;

        /// The delete command's Done. True when a delete was waiting. The VM's delete ending its snapshots is
        /// EndWithItsVm.
        public static bool MarkDeleted(this VmSnapshot snapshot) =>
            snapshot.Pending is { Kind: VmSnapshotOperationKind.Delete } delete
            && snapshot.EndOperation(delete.Id, VmSnapshotOperationEnd.Done(null)) != null;
    }
}

namespace Titan.Domain.Aggregates.Volume
{
    /// A volume's and a volume snapshot's requests end by their own command's report
    /// (titan-ops architecture/resource-lifecycle.md#shape); these replay the worker's reports under the one-call names
    /// test setups used before, through the real EndOperation. A replay for a request not waiting does nothing.
    public static class VolumeProvisionedOnTheProvider
    {
        /// The create (or restore) command's Done with the volume name.
        public static void MarkCreated(this Volume volume, string externalId)
        {
            if (volume.Pending is { Kind: VolumeOperationKind.Create } create)
            {
                volume.EndOperation(create.Id, VolumeOperationEnd.Done(externalId, null));
            }
        }

        /// The attach command's Done, reading the volume used by that VM.
        public static void MarkAttached(this Volume volume, Guid vmId)
        {
            if (volume.Pending is { Kind: VolumeOperationKind.Attach } attach)
            {
                volume.EndOperation(attach.Id, VolumeOperationEnd.Done(null, new VolumeObservation(vmId, false)));
            }
        }

        /// The delete command's Done.
        public static VolumeOperationEnded? MarkDeleted(this Volume volume) =>
            volume.Pending is { Kind: VolumeOperationKind.Delete } delete
                ? volume.EndOperation(delete.Id, VolumeOperationEnd.Done(null, null))
                : null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this Volume volume, string? reason, string? detail, string? externalId = null)
        {
            if (volume.Pending is { Kind: VolumeOperationKind.Create } create)
            {
                volume.EndOperation(create.Id, VolumeOperationEnd.Failed(externalId, null, reason, detail));
            }
        }

        /// The snapshot's create command's Done with the snapshot's provider id.
        public static void MarkSnapshotCreated(this Volume volume, Guid snapshotId, string externalId)
        {
            VolumeSnapshot snapshot = volume.Snapshots.Single(s => s.Id == snapshotId);
            if (snapshot.Pending is { Kind: VolumeSnapshotOperationKind.Create } create)
            {
                volume.EndSnapshotOperation(snapshotId, create.Id, VolumeSnapshotOperationEnd.Done(externalId));
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
                snapshot.EndOperation(create.Id, VolumeSnapshotOperationEnd.Done(externalId));
            }
        }

        /// The create command's give-up.
        public static void MarkRequestFailed(this VolumeSnapshot snapshot, string? reason, string? detail)
        {
            if (snapshot.Pending is { Kind: VolumeSnapshotOperationKind.Create } create)
            {
                snapshot.EndOperation(create.Id, VolumeSnapshotOperationEnd.Failed(null, reason, detail));
            }
        }

        /// Incus no longer has it after Titan's delete: its own delete's Done, or its volume's.
        public static bool MarkDeleted(this VolumeSnapshot snapshot) => snapshot.EndWithItsVolume();
    }
}

namespace Titan.Domain.Aggregates.LoadBalancer
{
    /// A load balancer's request ends by its own command's report (titan-ops architecture/resource-lifecycle.md#shape);
    /// these replay the worker's reports under the one-call names test setups used before, through the real EndOperation.
    /// A replay for a request not waiting does nothing, like the report it stands for.
    public static class LoadBalancerProvisionedOnTheProvider
    {
        /// The create command's Done with the VIP. True when it ended the create.
        public static bool MarkCreated(this LoadBalancer loadBalancer, string externalId) =>
            loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Create } create
            && loadBalancer.EndOperation(create.Id, LoadBalancerOperationEnd.Done(externalId)) != null;

        /// The update command's Done for its request. True when that update was the one waiting.
        public static bool MarkUpdated(this LoadBalancer loadBalancer, Guid requestId) =>
            loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Update } update
            && update.Id == requestId
            && loadBalancer.EndOperation(requestId, LoadBalancerOperationEnd.Done(null)) != null;

        /// The delete command's Done. True when a delete was waiting.
        public static bool MarkDeleted(this LoadBalancer loadBalancer) =>
            loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Delete } delete
            && loadBalancer.EndOperation(delete.Id, LoadBalancerOperationEnd.Done(null)) != null;

        /// The create command's give-up, before Incus had the VIP.
        public static void MarkRequestFailed(this LoadBalancer loadBalancer, string? reason, string? detail)
        {
            if (loadBalancer.Pending is { Kind: LoadBalancerOperationKind.Create } create)
            {
                loadBalancer.EndOperation(create.Id, LoadBalancerOperationEnd.Failed(null, reason, detail));
            }
        }
    }
}

namespace Titan.Domain.Aggregates.VmTemplate
{
    /// A template's sync in a cluster ends by its own command's report (titan-ops architecture/resource-lifecycle.md#shape);
    /// these replay the worker's reports under the one-call names test setups used before, through the real
    /// EndSyncOperation. A replay for a sync not waiting does what the old route does with that report.
    public static class VmTemplateProvisionedOnTheProvider
    {
        /// The sync command's Done with the alias. True when it ended a waiting sync; with none waiting it records the
        /// alias seen, as the old route's Created does.
        public static bool MarkCreatedIn(this VmTemplate template, string clusterId, string externalTemplateId)
        {
            Values.PendingOperation<VmTemplateOperationKind>? waiting = template.Clusters.FirstOrDefault(c => c.ClusterId == clusterId)?.Pending;
            if (waiting != null)
            {
                return template.EndSyncOperation(clusterId, waiting.Id, VmTemplateOperationEnd.Done(externalTemplateId)) != null;
            }
            template.MarkAliasObservedIn(clusterId, externalTemplateId);
            return false;
        }

        /// The sync command's give-up, before Incus had the alias. True when a sync was waiting.
        public static bool MarkRequestFailedIn(this VmTemplate template, string clusterId, string? reason, string? detail)
        {
            Values.PendingOperation<VmTemplateOperationKind>? waiting = template.Clusters.FirstOrDefault(c => c.ClusterId == clusterId)?.Pending;
            return waiting != null
                   && template.EndSyncOperation(clusterId, waiting.Id, VmTemplateOperationEnd.Failed(null, reason, detail)) != null;
        }
    }
}
