using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// The report that ends a firewall rule's request (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the rule's provider id when the worker found it in the ACL, the ACL rule state it read, and for a Failed the
/// fixed reason and the raw detail.
public sealed record FirewallRuleRequestEnd(
    RequestOutcome Outcome,
    string? ExternalId,
    FirewallRuleState? ObservedState,
    string? Reason,
    string? Detail)
{
    public static FirewallRuleRequestEnd Done(string? externalId, FirewallRuleState? observedState) =>
        new(RequestOutcome.Done, externalId, observedState, null, null);

    public static FirewallRuleRequestEnd Failed(string? externalId, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, null, reason, detail);
}
