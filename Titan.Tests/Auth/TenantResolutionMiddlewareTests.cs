using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Moq;
using Titan.Application.Commands.Billing.Wallet;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Repositories.Billing;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Auth;

namespace Titan.Tests.Auth;

public class TenantResolutionMiddlewareTests
{
    private readonly Mock<ITenantRepository> _repo = new();
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<RequestDelegate> _next = new();

    // The middleware takes the three concrete command handlers (nothing to mock), so they run for real over the
    // repository mocks. "Bootstrap ran with X" is read off the tenant it added; "the wallet was opened" off the
    // wallet it added.
    private TenantResolutionMiddleware CreateMiddleware() => new(
        _repo.Object,
        new BootstrapTenantCommandHandler(_repo.Object),
        new ApproveDeclaredAccountCommandHandler(_repo.Object),
        new OpenWalletCommandHandler(_walletRepo.Object, new CreditOptions { Currency = "VND" }));

    private void VerifyNoWalletOpened() =>
        _walletRepo.Verify(w => w.Add(It.IsAny<Wallet>()), Times.Never);

    // Records "wallet" when a wallet is saved and "tenant" when the tenant is, so a test reads the order.
    private List<string> RecordWalletAndTenantSaves()
    {
        var calls = new List<string>();
        _walletRepo.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("wallet")).Returns(Task.CompletedTask);
        _repo.Setup(r => r.SaveAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("tenant")).Returns(Task.CompletedTask);
        return calls;
    }

    private void VerifyNoBootstrap() =>
        _repo.Verify(r => r.Add(It.IsAny<Tenant>()), Times.Never);

    private void VerifyBootstrapped(Func<Tenant, bool> match) =>
        _repo.Verify(r => r.Add(It.Is<Tenant>(t => match(t))), Times.Once);

    private static bool OwnedBy(Tenant t, string keycloakUserId) =>
        t.Members.Any(m => m.KeycloakUserId == keycloakUserId);

    [Fact]
    public async Task Unauthenticated_SkipsDbQuery_CallsNext()
    {
        var ctx = BuildContext(authenticated: false, method: "POST");

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _repo.Verify(r => r.FindTenantByMemberAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoBootstrap();
        _next.Verify(n => n(ctx), Times.Once);
    }

    [Fact]
    public async Task FirstLogin_BootstrapsAndSetsTenantId()
    {
        var tenantId = Guid.NewGuid();
        _repo.SetupSequence(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((ValueTuple<Guid, TenantStatus>?)null)
             .ReturnsAsync((tenantId, TenantStatus.Active));


        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        VerifyBootstrapped(t => OwnedBy(t, "user-1"));
        Assert.Equal(tenantId, ctx.Items["TenantId"]);
        _next.Verify(n => n(ctx), Times.Once);
    }

    [Fact]
    public async Task FirstLogin_PassesEmailClaimToBootstrap()
    {
        _repo.SetupSequence(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((ValueTuple<Guid, TenantStatus>?)null)
             .ReturnsAsync((Guid.NewGuid(), TenantStatus.Active));


        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Method = "GET";
        var claims = new[]
        {
            new Claim("sub", "user-1"),
            new Claim("preferred_username", "alice"),
            new Claim("email", "alice@example.com")
        };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        VerifyBootstrapped(t => OwnedBy(t, "user-1") && t.Name == "alice" && t.Email == "alice@example.com");
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#approval-is-activation: a first login of an account with the realm role
    // auto-approve is created pending and approved in the same request, wallet first — the request already sees an
    // Active tenant with a wallet.
    [Fact]
    public async Task FirstLogin_DeclaredAccount_EndsActive_WithAWallet_OpenedBeforeTheApproval()
    {
        Tenant? created = null;
        _repo.Setup(r => r.Add(It.IsAny<Tenant>())).Callback<Tenant>(t => created = t);
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => created);
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync(() =>
             {
                 if (created == null)
                 {
                     return null;
                 }
                 return (created.Id, created.Status);
             });
        var calls = RecordWalletAndTenantSaves();

        var ctx = DeclaredAccountContext("POST");

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        Assert.NotNull(created);
        Assert.Equal(TenantStatus.Active, created!.Status);
        _walletRepo.Verify(w => w.Add(It.Is<Wallet>(x => x.TenantId == created.Id)), Times.Once);
        // bootstrap saves the pending tenant, then the wallet, then the approval
        Assert.Equal(new[] { "tenant", "wallet", "tenant" }, calls);
        _next.Verify(n => n(ctx), Times.Once); // a write passes: the request sees the approved tenant
        Assert.Equal(created.Id, ctx.Items["TenantId"]);
    }

    // A self-signup waits for an admin, even with a verified email: only the realm's role approves on sign-in.
    [Fact]
    public async Task FirstLogin_VerifiedEmailWithoutTheRole_StaysPending_WithoutAWallet()
    {
        _repo.SetupSequence(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((ValueTuple<Guid, TenantStatus>?)null)
             .ReturnsAsync((Guid.NewGuid(), TenantStatus.PendingApproval));

        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.Request.Method = "GET";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "user-1"),
            new Claim("preferred_username", "bob"),
            new Claim("email", "bob@openvicloud.com"),
            new Claim("email_verified", "true"),
        }, authenticationType: "test"));

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        VerifyBootstrapped(t => OwnedBy(t, "user-1") && t.Name == "bob" && t.Email == "bob@openvicloud.com"
                                && t.Status == TenantStatus.PendingApproval);
        VerifyNoWalletOpened();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ActiveTenant_AllMethods_CallsNext(string method)
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.Active));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: method);

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        VerifyNoBootstrap();
        _next.Verify(n => n(ctx), Times.Once);
        Assert.Equal(tenantId, ctx.Items["TenantId"]);
    }

    [Fact]
    public async Task SuspendedTenant_GetRequest_CallsNext()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.Suspended));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _next.Verify(n => n(ctx), Times.Once);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task SuspendedTenant_MutationMethod_Returns403_DoesNotCallNext(string method)
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.Suspended));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: method);

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _next.Verify(n => n(ctx), Times.Never);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal("TENANT_SUSPENDED", body.RootElement.GetProperty("code").GetString());
    }

    // Opening a shell session is a GET upgrade, but it acts inside the VM: a suspended or unapproved tenant may not
    // open one (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session).
    [Theory]
    [InlineData(TenantStatus.Suspended, "TENANT_SUSPENDED")]
    [InlineData(TenantStatus.PendingApproval, "TENANT_PENDING_APPROVAL")]
    public async Task WebSocketUpgrade_OfATenantThatMayNotWrite_Returns403_DoesNotCallNext(TenantStatus status, string code)
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, status));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");
        ctx.Features.Set<IHttpWebSocketFeature>(new WebSocketUpgradeRequest());

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _next.Verify(n => n(ctx), Times.Never);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    // A refused upgrade answered with a chunked body reaches the CLI as the edge's 502, without the code
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#error-envelope).
    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.PendingApproval)]
    public async Task Refusal_DeclaresTheByteLengthOfItsBody(TenantStatus status)
    {
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((Guid.NewGuid(), status));
        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");
        ctx.Features.Set<IHttpWebSocketFeature>(new WebSocketUpgradeRequest());

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.NotNull(ctx.Response.ContentLength);
        Assert.Equal(ctx.Response.Body.Length, ctx.Response.ContentLength);
    }

    [Fact]
    public async Task WebSocketUpgrade_OfAnActiveTenant_CallsNext()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.Active));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");
        ctx.Features.Set<IHttpWebSocketFeature>(new WebSocketUpgradeRequest());

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _next.Verify(n => n(ctx), Times.Once);
    }

    private sealed class WebSocketUpgradeRequest : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task PendingApprovalTenant_GetRequest_CallsNext()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.PendingApproval));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _next.Verify(n => n(ctx), Times.Once); // portal must be able to read its own pending status
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PendingApprovalTenant_MutationMethod_Returns403_DoesNotCallNext(string method)
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.PendingApproval));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: method);

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _next.Verify(n => n(ctx), Times.Never);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal("TENANT_PENDING_APPROVAL", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PendingApproval_DeclaredAccount_OpensTheWalletThenApproves_AndPassesThrough()
    {
        var pending = Tenant.CreatePending("bob", "user-1", "bob@openvicloud.com");
        _repo.SetupSequence(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((pending.Id, TenantStatus.PendingApproval))
             .ReturnsAsync((pending.Id, TenantStatus.Active));
        _repo.Setup(r => r.FindByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        var calls = RecordWalletAndTenantSaves();

        var ctx = DeclaredAccountContext("POST"); // a write — proves the self-heal cleared the PendingApproval gate

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        Assert.Equal(TenantStatus.Active, pending.Status);
        Assert.Equal(new[] { "wallet", "tenant" }, calls);
        _next.Verify(n => n(ctx), Times.Once);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(pending.Id, ctx.Items["TenantId"]);
    }

    // A process that died after opening the wallet left the tenant pending: the next request opens nothing new and
    // approves it.
    [Fact]
    public async Task PendingApproval_WhoseWalletIsAlreadyOpen_IsApproved_WithoutASecondWallet()
    {
        var pending = Tenant.CreatePending("bob", "user-1", "bob@openvicloud.com");
        _repo.SetupSequence(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((pending.Id, TenantStatus.PendingApproval))
             .ReturnsAsync((pending.Id, TenantStatus.Active));
        _repo.Setup(r => r.FindByIdAsync(pending.Id, It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        _walletRepo.Setup(w => w.GetByTenantForUpdateAsync(pending.Id, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Wallet.Open(pending.Id, "VND"));

        await CreateMiddleware().InvokeAsync(DeclaredAccountContext("GET"), _next.Object);

        Assert.Equal(TenantStatus.Active, pending.Status);
        VerifyNoWalletOpened();
    }

    [Fact]
    public async Task PendingApproval_WithoutTheRole_DoesNotSelfHeal_WriteStill403()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.PendingApproval));

        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.Request.Method = "POST";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "user-1"),
            new Claim("email", "bob@openvicloud.com"),
            new Claim("email_verified", "true"),
        }, authenticationType: "test"));

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoWalletOpened();
        _next.Verify(n => n(ctx), Times.Never);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ActiveTenant_SetsTenantIdInItems()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-1", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.Active));

        var ctx = BuildContext(authenticated: true, userId: "user-1", method: "GET");

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        Assert.Equal(tenantId, ctx.Items["TenantId"]);
    }

    // sub is Keycloak's default user-id claim.

    [Fact]
    public async Task SubClaim_ResolvesUserIdCorrectly()
    {
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.FindTenantByMemberAsync("user-sub", It.IsAny<CancellationToken>()))
             .ReturnsAsync((tenantId, TenantStatus.Active));

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Method = "GET";
        var claims = new[] { new Claim("sub", "user-sub") };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));

        await CreateMiddleware().InvokeAsync(ctx, _next.Object);

        Assert.Equal(tenantId, ctx.Items["TenantId"]);
        _next.Verify(n => n(ctx), Times.Once);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // The role reaches the principal as a flat role claim (KeycloakRealmRolesClaimsTransformation).
    private static DefaultHttpContext DeclaredAccountContext(string method)
    {
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.Request.Method = method;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "user-1"),
            new Claim("preferred_username", "bob"),
            new Claim("email", "bob@openvicloud.com"),
            new Claim(ClaimTypes.Role, TenantResolutionMiddleware.AutoApproveRole),
        }, authenticationType: "test"));
        return ctx;
    }

    private static DefaultHttpContext BuildContext(bool authenticated, string method = "GET", string userId = "user-1", string email = "user@example.com")
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Method = method;

        if (authenticated)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("email", email)
            };
            var identity = new ClaimsIdentity(claims, authenticationType: "test");
            ctx.User = new ClaimsPrincipal(identity);
        }

        return ctx;
    }
}
