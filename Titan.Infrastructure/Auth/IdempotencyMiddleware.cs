using FastEndpoints;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Titan.Application.Services.Platform;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Auth;

/// <summary>
/// A POST carrying an <c>Idempotency-Key</c> is deduplicated per (TenantId, Key): identical retries replay the
/// stored response. Must run after TenantResolutionMiddleware, which sets TenantId.
/// </summary>
public class IdempotencyMiddleware(IIdempotencyService idempotency) : IMiddleware
{
    private const string HeaderName = "Idempotency-Key";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // A create without a key is refused before anything runs (titan-ops architecture/http-contract.md#idempotency-key);
        // for every other request the key is optional.
        bool endpointRequiresKey = RequiresKey(context);
        bool keyIsMissing = string.IsNullOrWhiteSpace(context.Request.Headers[HeaderName]);
        if (endpointRequiresKey && keyIsMissing)
        {
            throw new BadRequestException("IDEMPOTENCY_KEY_REQUIRED",
                $"This request creates a resource and must carry an {HeaderName} header.");
        }

        if (!HttpMethods.IsPost(context.Request.Method)
            || !context.Request.Headers.TryGetValue(HeaderName, out var headerValues)
            || string.IsNullOrWhiteSpace(headerValues))
        {
            await next(context);
            return;
        }

        // Idempotency is scoped per tenant; requests without a tenant context are not supported.
        if (context.Items["TenantId"] is not Guid tenantId)
        {
            await next(context);
            return;
        }

        var key = headerValues.ToString();
        var requestHash = await ComputeRequestHashAsync(context.Request);

        var decision = await idempotency.BeginAsync(tenantId, key, requestHash, context.RequestAborted);

        switch (decision)
        {
            case IdempotencyDecision.KeyReused:
                throw new BusinessException("IDEMPOTENCY_KEY_REUSED",
                    "Idempotency-Key already used with a different request body.");

            case IdempotencyDecision.InProgress:
                throw new ConflictException("IDEMPOTENCY_IN_PROGRESS",
                    "A request with this Idempotency-Key is still being processed.");

            case IdempotencyDecision.Replay replay:
                context.Response.StatusCode = replay.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(replay.Body, context.RequestAborted);
                return;

            case IdempotencyDecision.Proceed:
                await CaptureAndStoreAsync(context, next, tenantId, key);
                return;

            default:
                throw new InvalidOperationException($"Unhandled idempotency decision: {decision.GetType().Name}");
        }
    }

    /// True when the matched endpoint class implements IRequiresIdempotencyKey (titan-ops architecture/http-contract.md#idempotency-key).
    private static bool RequiresKey(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint == null)
        {
            return false; // no route matched — the request will 404 on its own
        }

        EndpointDefinition? definition = endpoint.Metadata.GetMetadata<EndpointDefinition>();
        if (definition == null)
        {
            return false; // not a FastEndpoints endpoint (health checks, OpenAPI document, ...)
        }

        return typeof(IRequiresIdempotencyKey).IsAssignableFrom(definition.EndpointType);
    }

    private async Task CaptureAndStoreAsync(HttpContext context, RequestDelegate next, Guid tenantId, string key)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        catch
        {
            // Endpoint threw (e.g. a domain exception). Restore the real stream so the
            // exception handler can write the error, and free the key for retry.
            context.Response.Body = originalBody;
            await idempotency.AbandonAsync(tenantId, key, context.RequestAborted);
            throw;
        }

        context.Response.Body = originalBody;

        var statusCode = context.Response.StatusCode;
        buffer.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);

        // 5xx is transient — free the key so the client can re-attempt. Deterministic
        // outcomes (2xx/4xx) are stored and replayed on retry.
        if (statusCode >= 500)
            await idempotency.AbandonAsync(tenantId, key, context.RequestAborted);
        else
            await idempotency.CompleteAsync(tenantId, key, statusCode, body, context.RequestAborted);

        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(originalBody, context.RequestAborted);
    }

    private static async Task<string> ComputeRequestHashAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Seek(0, SeekOrigin.Begin);

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        request.Body.Seek(0, SeekOrigin.Begin); // rewind so the endpoint can read the body

        var hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash);
    }
}
