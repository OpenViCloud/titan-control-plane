using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// The report that ends a firewall rule's operation (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the rule's provider id when the worker found it in the ACL, the ACL rule state it read, and for a Failed the
/// fixed reason and the raw detail.
public sealed record FirewallRuleOperationEnd(
    OperationOutcome Outcome,
    string? ExternalId,
    FirewallRuleState? ObservedState,
    string? Reason,
    string? Detail)
{
    public static FirewallRuleOperationEnd Done(string? externalId, FirewallRuleState? observedState) =>
        new(OperationOutcome.Done, externalId, observedState, null, null);

    public static FirewallRuleOperationEnd Failed(string? externalId, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, null, reason, detail);
}
