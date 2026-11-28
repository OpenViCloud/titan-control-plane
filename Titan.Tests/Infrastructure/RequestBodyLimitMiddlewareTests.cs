using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Titan.Application.Exceptions;
using Titan.Infrastructure.Endpoints;

namespace Titan.Tests.Infrastructure;

// A route's RequestBodyLimit (titan-ops features/ovc-function/design.md#code-storage): the server limit raised for that route
// only, a declared length over it refused before anything reads the body, and a chunked body stopped by Kestrel answered with
// the route's code rather than a 500.
public class RequestBodyLimitMiddlewareTests
{
    private const string Code = "FUNCTION_CODE_TOO_LARGE";
    private static readonly RequestBodyLimit Limit = new(1000, Code);

    private sealed class BodySizeFeature(bool isReadOnly) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; } = isReadOnly;
        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    private static (DefaultHttpContext Context, BodySizeFeature Feature) Request(RequestBodyLimit? limit, long? contentLength = null,
        bool readOnly = false)
    {
        var context = new DefaultHttpContext();
        var feature = new BodySizeFeature(readOnly);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        context.Request.ContentLength = contentLength;
        var metadata = limit is null ? new EndpointMetadataCollection() : new EndpointMetadataCollection(limit);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));
        return (context, feature);
    }

    [Fact]
    public async Task ARouteWithoutTheMetadata_KeepsTheServerLimit()
    {
        var (context, feature) = Request(limit: null, contentLength: 5000);
        bool called = false;

        await new RequestBodyLimitMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(30_000_000, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task ARouteWithTheMetadata_GetsItsOwnLimit()
    {
        var (context, feature) = Request(Limit, contentLength: 1000);
        long? seenByTheEndpoint = null;

        await new RequestBodyLimitMiddleware(_ => { seenByTheEndpoint = feature.MaxRequestBodySize; return Task.CompletedTask; })
            .InvokeAsync(context);

        Assert.Equal(1000, seenByTheEndpoint);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheLimit_IsRefusedWithoutCallingTheRest()
    {
        var (context, _) = Request(Limit, contentLength: 1001);
        bool called = false;

        var e = await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            new RequestBodyLimitMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context));

        Assert.False(called);
        Assert.Equal(new ErrorStatus(413, Code), ErrorStatus.For(e));
    }

    [Fact]
    public async Task AChunkedBodyKestrelStopsAtTheLimit_IsAnsweredWithTheRoutesCode()
    {
        var (context, _) = Request(Limit, contentLength: null);

        var e = await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            new RequestBodyLimitMiddleware(_ => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge))
                .InvokeAsync(context));

        Assert.Equal(new ErrorStatus(413, Code), ErrorStatus.For(e));
    }

    [Fact]
    public async Task AnotherBadRequest_PassesThroughUnchanged()
    {
        var (context, _) = Request(Limit, contentLength: 10);

        var e = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            new RequestBodyLimitMiddleware(_ => throw new BadHttpRequestException("Unexpected end of request content.", 400)).InvokeAsync(context));

        Assert.Equal(400, e.StatusCode);
    }

    [Fact]
    public async Task AFeatureAlreadyReadOnly_IsLeftAlone()
    {
        var (context, feature) = Request(Limit, contentLength: 10, readOnly: true);

        await new RequestBodyLimitMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.Equal(30_000_000, feature.MaxRequestBodySize);
    }
}
