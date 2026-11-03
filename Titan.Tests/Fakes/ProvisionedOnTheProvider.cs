// Test setup for a resource the provider already holds. The create command reports the id and the reconciler reports
// the Incus state (titan-ops architecture/desired-and-current.md#desired-and-current); these replay both reports in order,
// under the one-call names the setups used before the two were split. A volume or a snapshot has no state word on
// Incus, so its create command's Done is the whole of it.
//
// Each class lives in its aggregate's namespace so a test that uses the aggregate sees the extension without a using.

namespace Titan.Domain.Aggregates.Network
{
    public static class NetworkProvisionedOnTheProvider
    {
        public static bool MarkActive(this Network network, string externalId, string? cidr, string? gateway)
        {
            network.MarkCreated(externalId, cidr, gateway);
            return network.MarkObserved(NetworkStatus.Created);
        }
    }

    public static class FirewallRuleProvisionedOnTheProvider
    {
        /// The create command's id, then the reconciler's first read of the rule in its ACL.
        public static bool MarkActive(this FirewallRule rule, string externalId)
        {
            rule.MarkCreated(externalId);
            return rule.MarkObserved(FirewallRuleState.enabled);
        }
    }
}

namespace Titan.Domain.Aggregates.Vm
{
    /// A VM's requests end by their own command's report (titan-ops architecture/resource-lifecycle.md#shape); these replay
    /// the worker's reports under the one-call names test setups used before, through the real EndRequest and
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
            if (vm.Pending is { Kind: VmRequestKind.Create } create)
            {
                vm.EndRequest(create.Id, VmRequestEnd.Done(externalId, null));
            }
        }

        /// The reconciler's observation, by its parts.
        public static void MarkObserved(this Vm vm, VmState state, string? ipAddress, string? publicIpv6Address = null) =>
            vm.MarkObserved(new VmObservation(state, ipAddress, publicIpv6Address));

        /// The delete command's Done.
        public static VmRequestEnded? MarkDeleted(this Vm vm) =>
            vm.Pending is { Kind: VmRequestKind.Delete } delete
                ? vm.EndRequest(delete.Id, VmRequestEnd.Done(null, null))
                : null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this Vm vm, string? reason, string? detail, string? externalId = null)
        {
            if (vm.Pending is { Kind: VmRequestKind.Create } create)
            {
                vm.EndRequest(create.Id, VmRequestEnd.Failed(externalId, null, reason, detail));
            }
        }
    }
}

namespace Titan.Domain.Aggregates.Volume
{
    /// A volume's and a volume snapshot's requests end by their own command's report
    /// (titan-ops architecture/resource-lifecycle.md#shape); these replay the worker's reports under the one-call names
    /// test setups used before, through the real EndRequest. A replay for a request not waiting does nothing.
    public static class VolumeProvisionedOnTheProvider
    {
        /// The create (or restore) command's Done with the volume name.
        public static void MarkCreated(this Volume volume, string externalId)
        {
            if (volume.Pending is { Kind: VolumeRequestKind.Create } create)
            {
                volume.EndRequest(create.Id, VolumeRequestEnd.Done(externalId, null));
            }
        }

        /// The attach command's Done, reading the volume used by that VM.
        public static void MarkAttached(this Volume volume, Guid vmId)
        {
            if (volume.Pending is { Kind: VolumeRequestKind.Attach } attach)
            {
                volume.EndRequest(attach.Id, VolumeRequestEnd.Done(null, new VolumeObservation(vmId, false)));
            }
        }

        /// The delete command's Done.
        public static VolumeRequestEnded? MarkDeleted(this Volume volume) =>
            volume.Pending is { Kind: VolumeRequestKind.Delete } delete
                ? volume.EndRequest(delete.Id, VolumeRequestEnd.Done(null, null))
                : null;

        /// The create command's give-up.
        public static void MarkRequestFailed(this Volume volume, string? reason, string? detail, string? externalId = null)
        {
            if (volume.Pending is { Kind: VolumeRequestKind.Create } create)
            {
                volume.EndRequest(create.Id, VolumeRequestEnd.Failed(externalId, null, reason, detail));
            }
        }

        /// The snapshot's create command's Done with the snapshot's provider id.
        public static void MarkSnapshotCreated(this Volume volume, Guid snapshotId, string externalId)
        {
            VolumeSnapshot snapshot = volume.Snapshots.Single(s => s.Id == snapshotId);
            if (snapshot.Pending is { Kind: VolumeSnapshotRequestKind.Create } create)
            {
                volume.EndSnapshotRequest(snapshotId, create.Id, VolumeSnapshotRequestEnd.Done(externalId));
            }
        }
    }

    /// The same replays on one snapshot, for tests that build a snapshot's history directly.
    public static class VolumeSnapshotProvisionedOnTheProvider
    {
        /// The create command's Done with the snapshot's provider id.
        public static void MarkCreated(this VolumeSnapshot snapshot, string externalId)
        {
            if (snapshot.Pending is { Kind: VolumeSnapshotRequestKind.Create } create)
            {
                snapshot.EndRequest(create.Id, VolumeSnapshotRequestEnd.Done(externalId));
            }
        }

        /// The create command's give-up.
        public static void MarkRequestFailed(this VolumeSnapshot snapshot, string? reason, string? detail)
        {
            if (snapshot.Pending is { Kind: VolumeSnapshotRequestKind.Create } create)
            {
                snapshot.EndRequest(create.Id, VolumeSnapshotRequestEnd.Failed(null, reason, detail));
            }
        }

        /// Incus no longer has it after Titan's delete: its own delete's Done, or its volume's.
        public static bool MarkDeleted(this VolumeSnapshot snapshot) => snapshot.EndWithItsVolume();
    }
}
