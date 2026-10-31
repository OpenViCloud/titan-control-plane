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
    public static class VmProvisionedOnTheProvider
    {
        /// The create command's id, then the reconciler's Running.
        public static VmIntent? MarkRunning(this Vm vm, string externalId, string? ipAddress, string? publicIpv6Address = null)
        {
            vm.MarkCreated(externalId);
            return vm.MarkObserved(VmState.Running, ipAddress, publicIpv6Address);
        }

        public static VmIntent? MarkStopped(this Vm vm) => vm.MarkObserved(VmState.Stopped, null);
    }
}
