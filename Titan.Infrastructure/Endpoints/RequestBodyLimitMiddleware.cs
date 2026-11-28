using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Titan.Application.Exceptions;

namespace Titan.Infrastructure.Endpoints;

/// Applies a route's RequestBodyLimit (titan-ops features/ovc-function/design.md#code-storage). Runs before
/// IdempotencyMiddleware, the first reader of a body, so the limit is in place before any byte is read.
public sealed class RequestBodyLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        RequestBodyLimit? limit = context.GetEndpoint()?.Metadata.GetMetadata<RequestBodyLimit>();
        if (limit is null)
        {
            await next(context);
            return;
        }

        IHttpMaxRequestBodySizeFeature? maxBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySize is { IsReadOnly: false })
        {
            maxBodySize.MaxRequestBodySize = limit.MaxBytes;
        }
        if (context.Request.ContentLength > limit.MaxBytes)
        {
            throw TooLarge(limit);
        }

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body has no Content-Length: Kestrel stops it at the limit while it is read.
            throw TooLarge(limit);
        }
    }

    private static PayloadTooLargeException TooLarge(RequestBodyLimit limit) =>
        new(limit.Code, $"The request body is larger than {limit.MaxBytes} bytes.");
}
