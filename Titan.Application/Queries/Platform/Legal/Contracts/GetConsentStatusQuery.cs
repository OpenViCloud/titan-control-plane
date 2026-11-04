namespace Titan.Application.Queries.Platform.Legal.Contracts;

// The user is the caller (Keycloak sub from the request context), so the query carries no parameters.
public record GetConsentStatusQuery();
