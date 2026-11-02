// Test setup for a resource the provider already holds. The create command reports the id and the reconciler reports
// the Incus state (titan-ops architecture/desired-and-current.md#desired-and-current); these replay both reports in order,
// under the one-call names the setups used before the two were split. A volume or a snapshot needs no helper: Incus
// has no state word for it, so its create command's report is the whole of it.
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
