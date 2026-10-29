using System.Text;
using Microsoft.AspNetCore.Http;
using Moq;
using Titan.Application.Services.Platform;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Auth;

namespace Titan.Tests.Auth;

public class IdempotencyMiddlewareTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string Key = "k1";

    private readonly Mock<IIdempotencyService> _idem = new();

    private IdempotencyMiddleware CreateMiddleware() => new(_idem.Object);

    [Fact]
    public async Task NoHeader_CallsNext_DoesNotTouchService()
    {
        var ctx = BuildContext(method: "POST", key: null, tenant: Tenant);
        var next = new Mock<RequestDelegate>();
        next.Setup(n => n(ctx)).Returns(Task.CompletedTask);

        await CreateMiddleware().InvokeAsync(ctx, next.Object);

        next.Verify(n => n(ctx), Times.Once);
        _idem.Verify(s => s.BeginAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetWithHeader_CallsNext_DoesNotTouchService()
    {
        var ctx = BuildContext(method: "GET", key: Key, tenant: Tenant);
        var next = new Mock<RequestDelegate>();
        next.Setup(n => n(ctx)).Returns(Task.CompletedTask);

        await CreateMiddleware().InvokeAsync(ctx, next.Object);

        next.Verify(n => n(ctx), Times.Once);
        _idem.Verify(s => s.BeginAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoTenantContext_CallsNext_DoesNotTouchService()
    {
        var ctx = BuildContext(method: "POST", key: Key, tenant: null);
        var next = new Mock<RequestDelegate>();
        next.Setup(n => n(ctx)).Returns(Task.CompletedTask);

        await CreateMiddleware().InvokeAsync(ctx, next.Object);

        next.Verify(n => n(ctx), Times.Once);
        _idem.Verify(s => s.BeginAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Proceed_RunsNext_CapturesResponse_CallsComplete()
    {
        _idem.Setup(s => s.BeginAsync(Tenant, Key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IdempotencyDecision.Proceed());
        var ctx = BuildContext(method: "POST", key: Key, tenant: Tenant);
        RequestDelegate next = async c =>
        {
            c.Response.StatusCode = 202;
            await c.Response.WriteAsync("{\"vmId\":\"x\"}");
        };

        await CreateMiddleware().InvokeAsync(ctx, next);

        _idem.Verify(s => s.CompleteAsync(Tenant, Key, 202, "{\"vmId\":\"x\"}", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(202, ctx.Response.StatusCode);
        Assert.Equal("{\"vmId\":\"x\"}", ReadBody(ctx));
    }

    [Fact]
    public async Task Replay_DoesNotRunNext_ReturnsStoredResponse()
    {
        _idem.Setup(s => s.BeginAsync(Tenant, Key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IdempotencyDecision.Replay(202, "{\"vmId\":\"original\"}"));
        var ctx = BuildContext(method: "POST", key: Key, tenant: Tenant);
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        await CreateMiddleware().InvokeAsync(ctx, next);

        Assert.False(nextCalled);
        Assert.Equal(202, ctx.Response.StatusCode);
        Assert.Equal("{\"vmId\":\"original\"}", ReadBody(ctx));
    }

    [Fact]
    public async Task KeyReused_ThrowsBusinessException()
    {
        _idem.Setup(s => s.BeginAsync(Tenant, Key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IdempotencyDecision.KeyReused());
        var ctx = BuildContext(method: "POST", key: Key, tenant: Tenant);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateMiddleware().InvokeAsync(ctx, _ => Task.CompletedTask));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", ex.Code);
    }

    [Fact]
    public async Task InProgress_ThrowsConflictException()
    {
        _idem.Setup(s => s.BeginAsync(Tenant, Key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IdempotencyDecision.InProgress());
        var ctx = BuildContext(method: "POST", key: Key, tenant: Tenant);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateMiddleware().InvokeAsync(ctx, _ => Task.CompletedTask));
        Assert.Equal("IDEMPOTENCY_IN_PROGRESS", ex.Code);
    }

    [Fact]
    public async Task ProceedThenNextThrows_AbandonsAndRethrows()
    {
        _idem.Setup(s => s.BeginAsync(Tenant, Key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IdempotencyDecision.Proceed());
        var ctx = BuildContext(method: "POST", key: Key, tenant: Tenant);
        RequestDelegate next = _ => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateMiddleware().InvokeAsync(ctx, next));

        _idem.Verify(s => s.AbandonAsync(Tenant, Key, It.IsAny<CancellationToken>()), Times.Once);
        _idem.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A 5xx abandons the key so the client may retry.
    [Fact]
    public async Task ProceedThen500_Abandons_DoesNotComplete()
    {
        _idem.Setup(s => s.BeginAsync(Tenant, Key, It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IdempotencyDecision.Proceed());
        var ctx = BuildContext(method: "POST", key: Key, tenant: Tenant);
        RequestDelegate next = async c =>
        {
            c.Response.StatusCode = 500;
            await c.Response.WriteAsync("oops");
        };

        await CreateMiddleware().InvokeAsync(ctx, next);

        _idem.Verify(s => s.AbandonAsync(Tenant, Key, It.IsAny<CancellationToken>()), Times.Once);
        _idem.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // titan-ops architecture/http-contract.md#idempotency-key: a create must carry a key. The middleware reads the
    // marker from the routed endpoint's FastEndpoints definition; ApiIdempotencyKeyTests pins that the real
    // routing table carries it for exactly the nine creates.

    [Fact]
    public async Task CreateEndpoint_WithoutAKey_Is400_AndNeverRuns()
    {
        var ctx = BuildContext("POST", key: null, tenant: Tenant);
        RouteTo<Titan.Infrastructure.Endpoints.Vm.CreateVmEndpoint>(ctx);
        var ran = false;

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateMiddleware().InvokeAsync(ctx, _ => { ran = true; return Task.CompletedTask; }));

        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", ex.Code);
        Assert.False(ran);
        _idem.VerifyNoOtherCalls();
    }

    [Fact] // an action that creates nothing: the key stays optional
    public async Task NonCreateEndpoint_WithoutAKey_StillPassesThrough()
    {
        var ctx = BuildContext("POST", key: null, tenant: Tenant);
        RouteTo<Titan.Infrastructure.Endpoints.Vm.StopVmEndpoint>(ctx);
        var ran = false;

        await CreateMiddleware().InvokeAsync(ctx, _ => { ran = true; return Task.CompletedTask; });

        Assert.True(ran);
    }

    private static void RouteTo<TEndpoint>(HttpContext ctx) =>
        ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new FastEndpoints.EndpointDefinition(typeof(TEndpoint), typeof(object), typeof(object))),
            typeof(TEndpoint).Name));

    // ── Helpers ───────────────────────────────────────────────────────────
    private static DefaultHttpContext BuildContext(string method, string? key, Guid? tenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"vm1\"}"));
        ctx.Request.ContentLength = ctx.Request.Body.Length;
        ctx.Response.Body = new MemoryStream();

        if (key is not null)
            ctx.Request.Headers["Idempotency-Key"] = key;
        if (tenant is Guid t)
            ctx.Items["TenantId"] = t;

        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }
}
