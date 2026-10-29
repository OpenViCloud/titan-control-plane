namespace Titan.Domain.Aggregates.Compute.Vm;

/// The provider id leaves only through a method that validated the state, together with its intent
/// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). ProviderId is non-null and
/// empty only for FinishLocally: a nullable would push `ticket.ProviderId!` into every handler.
public sealed record VmIntentTicket(VmIntentOutcome Outcome, Guid IntentId, string ProviderId);
