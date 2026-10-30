using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Legal;
using Titan.Application.Queries.Platform.Legal.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.Legal;

/// Whether the current caller still owes consent for the current document version.
public class GetConsentStatusQueryHandler(
    ILegalDocumentCatalog catalog,
    IAcceptanceRepository acceptances,
    ICurrentUser currentUser)
{
    public async Task<ConsentStatusQueryResult> HandleAsync(GetConsentStatusQuery query, CancellationToken ct = default)
    {
        var d = catalog.Current;
        // Bound to the CURRENT version: an acceptance of an older version does not clear the gate.
        var accepted = await acceptances.ExistsAsync(currentUser.KeycloakUserId, d.Key, d.Version, ct);
        return new ConsentStatusQueryResult(MustAcceptTerms: !accepted, DocumentKey: d.Key, DocumentVersion: d.Version);
    }
}
