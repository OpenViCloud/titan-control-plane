using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;

namespace Titan.Infrastructure.Endpoints.LoadBalancer;

/// The configuration rules the create and the update validators share; the aggregate holds the same ones
/// (titan-ops features/load-balancer/design.md#lb-resource-model).
public static class LoadBalancerSpecRules
{
    private static readonly string[] Protocols = ["TCP", "UDP"];

    public const string ListenersMessage =
        "'Listeners' must hold 1 to 10 listeners, each TCP or UDP with ports 1 to 65535, no protocol and port twice.";
    public const string BackendsMessage = "'Backends' must hold 1 to 20 distinct VMs.";

    public static bool ListenersAreValid(List<LoadBalancerListenerInput>? listeners)
    {
        if (listeners == null || listeners.Count < 1 || listeners.Count > LoadBalancerAggregate.MaxListeners)
        {
            return false;
        }
        bool eachIsValid = listeners.All(l => l != null && Protocols.Contains(l.Protocol) && IsPort(l.Port) && IsPort(l.TargetPort));
        if (!eachIsValid)
        {
            return false;
        }
        return listeners.Select(l => (l.Protocol, l.Port)).Distinct().Count() == listeners.Count;
    }

    public static bool BackendsAreValid(List<LoadBalancerBackendInput>? backends)
    {
        if (backends == null || backends.Count < 1 || backends.Count > LoadBalancerAggregate.MaxBackends)
        {
            return false;
        }
        bool eachIsValid = backends.All(b => b != null && b.VmId != Guid.Empty);
        return eachIsValid && backends.Select(b => b.VmId).Distinct().Count() == backends.Count;
    }

    private static bool IsPort(int port) => port >= 1 && port <= 65535;
}
