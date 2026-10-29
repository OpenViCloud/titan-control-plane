namespace Titan.Domain.Aggregates.Platform.Acceptance;

/// Click-wrap consent proof (titan-ops features/legal/design.md#consent-gate-per-user). Append-only: a new document
/// version produces a new row. Per user, not per tenant: every member accepts individually.
public class Acceptance
{
    public Guid Id { get; private set; }

    /// Keycloak `sub` of the accepting user.
    public string SubjectId { get; private set; } = default!;

    /// e.g. "terms". Bound with DocumentVersion so the proof is tied to the exact text the user saw.
    public string DocumentKey { get; private set; } = default!;
    public string DocumentVersion { get; private set; } = default!;

    public DateTimeOffset AcceptedAt { get; private set; }

    /// Nullable: a request behind a proxy without a forwarded address may not carry one, and that must not
    /// block recording the consent.
    public string? SourceIp { get; private set; }

    private Acceptance() { }

    public static Acceptance Record(string subjectId, string documentKey, string documentVersion, string? sourceIp)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("subjectId is required.", nameof(subjectId));
        if (string.IsNullOrWhiteSpace(documentKey))
            throw new ArgumentException("documentKey is required.", nameof(documentKey));
        if (string.IsNullOrWhiteSpace(documentVersion))
            throw new ArgumentException("documentVersion is required.", nameof(documentVersion));

        return new Acceptance
        {
            Id = Guid.NewGuid(),
            SubjectId = subjectId,
            DocumentKey = documentKey,
            DocumentVersion = documentVersion,
            AcceptedAt = DateTimeOffset.UtcNow,
            SourceIp = sourceIp
        };
    }
}
