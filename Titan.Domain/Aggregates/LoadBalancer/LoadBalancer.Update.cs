using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.LoadBalancer;

public partial class LoadBalancer
{
    /// Returns the new operation and the VIP: the update command is keyed on the operation. Starts only from Available,
    /// remembering that Incus holds the load balancer while the update waits.
    public OperationTicket Update(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        if (!RestsOnIncus)
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE",
                "Load balancer must be on Incus, with nothing waiting, to change.");
        }
        if (ListenersAre(listeners) && BackendsAre(backends.Select(vm => vm.Id).ToList()))
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "The load balancer already has this configuration.");
        }
        Ask(listeners, backends);
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, InstanceStatus);
        Status = LoadBalancerStatus.Updating;
        return OperationTicket.Of(operationId, ExternalId!);
    }

    /// The update command ended Done: Incus took what was asked, and the load balancer is Available. Ignored, returning
    /// false, when it names another operation than the update the load balancer waits on, as the late report of an update a
    /// delete replaced does.
    public bool UpdateSucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, LoadBalancerStatus.Updating))
        {
            return false;
        }
        RecordTheVip(externalId);
        Apply();
        Operation = Operation.Done();
        Status = LoadBalancerStatus.Available;
        return true;
    }

    /// The update command gave up: the load balancer rests again as Incus was seen holding it, with the reason, and what
    /// Incus took is asked for again. Ignored, returning false, when it names another operation than the update the load
    /// balancer waits on.
    public bool UpdateFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, LoadBalancerStatus.Updating))
        {
            return false;
        }
        LoadBalancerStatus? seen = WhatIncusHeld(externalId);
        AskWhatIsApplied();
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = seen ?? LoadBalancerStatus.NotFound;
        return true;
    }
}
