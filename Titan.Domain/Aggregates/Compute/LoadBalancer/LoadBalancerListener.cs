namespace Titan.Domain.Aggregates.Compute.LoadBalancer;

/// A listener row (titan-ops features/load-balancer/design.md#lb-resource-model). IsDesired: the customer's last request
/// has it. IsApplied: Incus took it. A row is either or both; LoadBalancer drops the one neither once a request ends.
public class LoadBalancerListener
{
    public Guid Id { get; private set; }
    public Guid LoadBalancerId { get; private set; }
    public LoadBalancerProtocol Protocol { get; private set; }
    public int Port { get; private set; }
    public int TargetPort { get; private set; }
    public bool IsDesired { get; private set; }
    public bool IsApplied { get; private set; }

    private LoadBalancerListener() { }

    internal static LoadBalancerListener Desired(Guid loadBalancerId, LoadBalancerListenerSpec spec) =>
        new()
        {
            Id = Guid.NewGuid(),
            LoadBalancerId = loadBalancerId,
            Protocol = spec.Protocol,
            Port = spec.Port,
            TargetPort = spec.TargetPort,
            IsDesired = true,
            IsApplied = false
        };

    internal bool Is(LoadBalancerListenerSpec spec) =>
        Protocol == spec.Protocol && Port == spec.Port && TargetPort == spec.TargetPort;

    internal void SetDesired(bool desired) => IsDesired = desired;
    internal void SetApplied() => IsApplied = true;
}
