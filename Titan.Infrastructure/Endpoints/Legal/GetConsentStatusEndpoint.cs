using FastEndpoints;
using Titan.Application.Queries.Platform.Legal;
using Titan.Application.Queries.Platform.Legal.Contracts;

namespace Titan.Infrastructure.Endpoints.Legal;

// Auth required: the flag is computed against the caller's Keycloak sub
// (titan-ops features/legal/design.md#consent-gate-per-user).
public class GetConsentStatusEndpoint(GetConsentStatusQueryHandler getConsentStatus) : EndpointWithoutRequest<ConsentStatusQueryResult>
{
    public override void Configure()
    {
        Get("v1/legal/consent-status");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendAsync(await getConsentStatus.HandleAsync(new GetConsentStatusQuery(), ct), cancellation: ct);
    }
}
