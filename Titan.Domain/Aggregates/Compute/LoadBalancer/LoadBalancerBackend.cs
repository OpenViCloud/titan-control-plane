namespace Titan.Domain.Aggregates.Compute.LoadBalancer;

/// A backend row: one VM of the load balancer's network (titan-ops features/load-balancer/design.md#backend-address).
/// ExternalVmId is the instance name the worker reads the MAC from; IsDesired/IsApplied as on LoadBalancerListener.
public class LoadBalancerBackend
{
    public Guid Id { get; private set; }
    public Guid LoadBalancerId { get; private set; }
    public Guid VmId { get; private set; }
    public string ExternalVmId { get; private set; } = default!;
    public bool IsDesired { get; private set; }
    public bool IsApplied { get; private set; }

    private LoadBalancerBackend() { }

    internal static LoadBalancerBackend Desired(Guid loadBalancerId, Guid vmId, string externalVmId) =>
        new()
        {
            Id = Guid.NewGuid(),
            LoadBalancerId = loadBalancerId,
            VmId = vmId,
            ExternalVmId = externalVmId,
            IsDesired = true,
            IsApplied = false
        };

    internal void SetDesired(bool desired) => IsDesired = desired;
    internal void SetApplied() => IsApplied = true;
}
