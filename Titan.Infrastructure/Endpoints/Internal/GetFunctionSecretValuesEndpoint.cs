using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.Function;
using Titan.Application.Queries.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The function worker's read of a version's secret values at its deploy
// (titan-ops features/ovc-function/design.md#function-secrets): the only answer that carries a value, so the token is
// checked before anything is read. Out of the public spec, as every internal route.
public class GetFunctionSecretValuesEndpoint(GetFunctionSecretValuesQueryHandler handler, IConfiguration config)
    : EndpointWithoutRequest<FunctionSecretValuesQueryResult>
{
    public override void Configure()
    {
        Get("internal/functions/{functionId:guid}/versions/{versionId:guid}/secret-values");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        FunctionSecretValuesQueryResult values = await handler.HandleAsync(
            new GetFunctionSecretValuesQuery(Route<Guid>("functionId"), Route<Guid>("versionId")), ct);
        await SendAsync(values, cancellation: ct);
    }
}
