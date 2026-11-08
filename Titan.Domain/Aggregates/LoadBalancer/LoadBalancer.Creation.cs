using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.LoadBalancer;

public partial class LoadBalancer
{
    /// The network is checked by the caller through Network.ProviderIdForLoadBalancer: this aggregate holds no Network type
    /// (titan-ops architecture/code-structure.md#module-boundary).
    public static LoadBalancer Create(
        Guid tenantId, string name, Guid networkId, string externalNetworkId,
        string networkSubnet, string clusterId, LoadBalancerVip vip, IReadOnlyList<LoadBalancerListenerSpec> listeners,
        IReadOnlyList<Vm.Vm> backends)
    {
        var loadBalancer = new LoadBalancer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NetworkId = networkId,
            ExternalNetworkId = externalNetworkId,
            NetworkSubnet = networkSubnet,
            ClusterId = clusterId,
            Name = name,
            VipIndex = vip.Index,
            Vip = vip.Address,
            Status = LoadBalancerStatus.Creating,
            CreatedAt = DateTimeOffset.UtcNow
        };
        loadBalancer.Ask(listeners, backends);
        loadBalancer.Operation = loadBalancer.Operation.Asked(Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        return loadBalancer;
    }

    /// The create command ended Done: records the VIP, gives the asked configuration its effect, and the load balancer is
    /// Available. A Done must leave the load balancer named on Incus, or nothing would be left to delete it through:
    /// without a VIP it is refused before anything is written (a worker that sends it has a bug, and does not retry a
    /// 422). A report that names another operation than the create the load balancer waits on is ignored and returns false.
    public bool CreateSucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, LoadBalancerStatus.Creating))
        {
            return false;
        }
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(externalId);
        if (doneWithoutAnId)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
        RecordTheVip(externalId);
        Apply();
        Operation = Operation.Done();
        Status = LoadBalancerStatus.Available;
        return true;
    }

    /// The create command gave up: records the reason and the VIP the worker found. Before Incus held a load balancer (the
    /// report brings no VIP) it is Failed, and may be deleted, not retried; after (the report names it) it is Available
    /// and holds what was asked, since the create's one write carries the whole configuration. Ignored, returning false,
    /// when it names another operation than the create the load balancer waits on.
    public bool CreateFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, LoadBalancerStatus.Creating))
        {
            return false;
        }
        bool bringsTheVip = RecordTheVip(externalId);
        if (bringsTheVip)
        {
            Apply();
        }
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (ExternalId is null)
        {
            Status = LoadBalancerStatus.Failed;
        }
        else
        {
            Status = LoadBalancerStatus.Available;
        }
        return true;
    }
}
