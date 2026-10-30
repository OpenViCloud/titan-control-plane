using System.Text.Json.Serialization;
using TopUpAggregate = Titan.Domain.Aggregates.TopUp.TopUp;
using Titan.Domain.Aggregates.TopUp;

namespace Titan.Application.Queries.Billing.TopUp.Contracts;

// The single wire shape for a TopUp on the /topups surface. `Reason` and `ConfirmedBy` are nullable
// because a real-money TopUp has no reason and a Pending one has no confirmer yet. Read side only
// (titan-ops architecture/code-structure.md#no-contract-reuse).
public record TopUpQueryResult(
    Guid Id, Guid TenantId, decimal Amount, string Currency, [property: JsonConverter(typeof(JsonStringEnumConverter))] TopUpStatus Status, string PaymentRef,
    string? Source, string? Reason, string? ConfirmedBy, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt)
{
    // Status/Source are enums in the domain but strings (the member name) on the wire. Amount is `long`
    // VND in the domain but `decimal` on the wire (titan-ops architecture/money-and-wallet.md#money-is-long-vnd).
    public static TopUpQueryResult From(TopUpAggregate t) =>
        new(t.Id, t.TenantId, t.Amount, t.Currency, t.Status, t.PaymentRef,
            t.Source?.ToString(), t.Reason, t.ConfirmedBy, t.CreatedAt, t.ConfirmedAt);
}
