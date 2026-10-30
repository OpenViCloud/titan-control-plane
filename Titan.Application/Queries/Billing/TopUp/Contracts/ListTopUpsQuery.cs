using Titan.Domain.Aggregates.TopUp;

namespace Titan.Application.Queries.Billing.TopUp.Contracts;

/// Admin TopUp history. `Source` null = every TopUp; a value filters to that Source
/// (PromotionalCredit = the grant history).
public record ListTopUpsQuery(TopUpSource? Source);
