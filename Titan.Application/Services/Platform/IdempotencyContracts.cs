// Same namespace as IdempotencyService on purpose (titan-ops architecture/code-structure.md#services-branch).

namespace Titan.Application.Services.Platform;

/// <summary>
/// What the caller (idempotency middleware) should do for an incoming request.
/// </summary>
public abstract record IdempotencyDecision
{
    /// <summary>First time this key is seen — an InProgress record was reserved; run the request.</summary>
    public sealed record Proceed : IdempotencyDecision;

    /// <summary>Key+body already completed — replay the stored response, do not run the request again.</summary>
    public sealed record Replay(int StatusCode, string Body) : IdempotencyDecision;

    /// <summary>Key seen, request still running — caller should return 409.</summary>
    public sealed record InProgress : IdempotencyDecision;

    /// <summary>Key reused with a different request body — caller should return 422.</summary>
    public sealed record KeyReused : IdempotencyDecision;
}
