using FastEndpoints;
using Titan.Application.Queries.Compute.FunctionSecret;
using Titan.Application.Queries.Compute.FunctionSecret.Contracts;

namespace Titan.Infrastructure.Endpoints.FunctionSecret;

public class ListFunctionSecretsEndpoint(ListFunctionSecretsQueryHandler handler)
    : EndpointWithoutRequest<List<FunctionSecretQueryResult>>
{
    public override void Configure() => Get("v1/tenant/function-secrets");

    public override async Task HandleAsync(CancellationToken ct) =>
        await SendOkAsync(await handler.HandleAsync(new ListFunctionSecretsQuery(), ct), ct);
}
