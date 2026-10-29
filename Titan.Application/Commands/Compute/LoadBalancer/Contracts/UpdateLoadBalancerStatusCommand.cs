namespace Titan.Application.Commands.Compute.LoadBalancer.Contracts;

// The worker's callback payload. Report is one of Observed (the reconciler: Exists), Created (the create command:
// ExternalId = the VIP), Updated (the update command: IntentId), Deleted (the delete command), or a command's
// give-up (Reason/Detail; an update's carries its IntentId, a create's the id when Incus has it)
// (titan-ops features/load-balancer/design.md#lb-lifecycle).
public record UpdateLoadBalancerStatusCommand(
    Guid LoadBalancerId,
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    Guid? IntentId = null,
    string? Reason = null,
    string? Detail = null);
