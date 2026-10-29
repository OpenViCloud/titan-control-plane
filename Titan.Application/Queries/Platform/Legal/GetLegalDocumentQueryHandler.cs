using Titan.Application.Services.Platform.Legal;
using Titan.Application.Queries.Platform.Legal.Contracts;

namespace Titan.Application.Queries.Platform.Legal;

/// The current document (content + version + effectiveAt) in the asked language — served anonymously for the public page.
/// Nothing to await — the document is git-tracked config (titan-ops features/legal/design.md#legal-document-in-code), not a database row — but it keeps
/// the HandleAsync shape every handler has, so a reader never has to check which kind this one is.
public class GetLegalDocumentQueryHandler(ILegalDocumentCatalog catalog)
{
    public Task<LegalDocumentQueryResult> HandleAsync(GetLegalDocumentQuery query, CancellationToken ct = default)
    {
        var d = catalog.Current;
        // "vi" in any case is Vietnamese; anything else, missing included, is English.
        var lang = string.Equals(query.Lang, "vi", StringComparison.OrdinalIgnoreCase) ? "vi" : "en";
        var text = lang == "vi" ? d.Vietnamese : d.English;
        return Task.FromResult(new LegalDocumentQueryResult(d.Key, d.Version, text.Title, text.Content, d.EffectiveAt, lang));
    }
}
