using FastEndpoints;
using Titan.Application.Queries.Platform.Legal;
using Titan.Application.Queries.Platform.Legal.Contracts;

namespace Titan.Infrastructure.Endpoints.Legal;

// Anonymous: a visitor must be able to read the terms before authenticating and accepting them.
public class GetLegalDocumentEndpoint(GetLegalDocumentQueryHandler getDocument) : EndpointWithoutRequest<LegalDocumentQueryResult>
{
    public override void Configure()
    {
        Get("v1/legal/document");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // ?lang=vi gives the Vietnamese text; anything else, missing included, gives English (titan-ops features/legal/design.md#legal-endpoints).
        string? lang = Query<string>("lang", isRequired: false);
        await SendAsync(await getDocument.HandleAsync(new GetLegalDocumentQuery(lang), ct), cancellation: ct);
    }
}
