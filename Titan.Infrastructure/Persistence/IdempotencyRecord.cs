namespace Titan.Infrastructure.Persistence;

/// <summary>
/// Not a domain aggregate (titan-ops features/public-api/design.md#idempotency-store).
/// </summary>
public class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Status { get; set; } = "InProgress";
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
