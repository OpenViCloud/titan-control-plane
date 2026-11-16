using System.Net.WebSockets;
using System.Text;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Exceptions;
using Titan.Application.Options;
using Titan.Application.Queries.Compute.Vm;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Auth;
using Titan.Infrastructure.Endpoints.Vm;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;
using Titan.Tests.Fakes;
using Titan.Tests.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure;

// The console endpoint (titan-ops features/vm/design.md#vm-console): a ticket stands in for the token a browser cannot
// send; only a bad ticket is refused before the upgrade, every later refusal is a 4xxx close whose reason is the code.
// The request runs as the ticket's taker, so the handlers and the activity see them. Silent about the pipeline:
// Factory.Create builds the endpoint without a host.
public class OpenVmConsoleEndpointTests : IAsyncLifetime
{
    private const string Ticket = "ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    // Bounds every wait on the endpoint, so a refusal that does not happen fails the test instead of hanging it.
    private static readonly TimeSpan RealWait = TimeSpan.FromSeconds(20);
    private static readonly Tenant Owner = TestTenant.WithRoomToSpare();
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly ClusterOptions _clusters = new();
    private readonly ServiceProvider _services;
    private readonly ShellSessionLimiter _limiter;
    private readonly HttpContextAccessor _accessor = new();
    private readonly Vm _vm;
    private WebSocket? _server;
    private WebSocket? _customer;
    private FakeIncusConsoleServer? _incus;
    private IncusClientFactory _factory = new(IncusTestKit.IdentityStore());

    public OpenVmConsoleEndpointTests()
    {
        string database = $"console-endpoint-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<TitanDbContext>(o => o.UseInMemoryDatabase(database));
        _services = services.BuildServiceProvider();
        _limiter = new ShellSessionLimiter(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<ShellSessionLimiter>.Instance);
        _vm = Vm.Create(Owner.Id, Guid.NewGuid(), "web-1", "s", "tpl-1", "local-mac", 20);
        _vm.MarkCreated("vmabc");
        _vm.MarkObserved(VmStatus.Running, "10.0.0.5");
        _vms.Setup(r => r.FindByIdAsync(_vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_vm);
        _tenants.Setup(r => r.FindTenantByMemberAsync("kc-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Owner.Id, TenantStatus.Active));
    }

    public async Task InitializeAsync()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        _incus = await FakeIncusConsoleServer.StartAsync(titan.Fingerprint);
        _factory = new IncusClientFactory(store);
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", _incus.Endpoint, _incus.Fingerprint));
        await IssueAsync(Ticket, _vm.Id);
    }

    public async Task DisposeAsync()
    {
        _server?.Dispose();
        _customer?.Dispose();
        if (_incus != null)
        {
            await _incus.DisposeAsync();
        }
        await _services.DisposeAsync();
    }

    private async Task IssueAsync(string ticket, Guid vmId)
    {
        using IServiceScope scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        await new ConsoleTicketStore(db, TimeProvider.System).IssueAsync(Owner.Id, vmId, "kc-1", ticket, CancellationToken.None);
    }

    private sealed class UpgradeRequest(WebSocket server) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(server);
    }

    private (OpenVmConsoleEndpoint Endpoint, TitanDbContext Db) Endpoint(string? ticket, bool upgrade = true)
    {
        (_server, _customer) = InMemoryWebSocket.Pair();
        IServiceScope scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        var currentUser = new CurrentUser(_accessor);
        WebSocket server = _server;
        var endpoint = Factory.Create<OpenVmConsoleEndpoint>(ctx =>
            {
                ctx.Request.RouteValues["vmId"] = _vm.Id.ToString();
                if (ticket != null)
                {
                    ctx.Request.QueryString = new QueryString("?ticket=" + ticket);
                }
                if (upgrade)
                {
                    ctx.Features.Set<IHttpWebSocketFeature>(new UpgradeRequest(server));
                }
                _accessor.HttpContext = ctx;
                ctx.RequestServices = new ServiceCollection().AddSingleton<IHttpContextAccessor>(_accessor).BuildServiceProvider();
            },
            new ConsoleTicketStore(db, TimeProvider.System),
            _tenants.Object,
            new GetVmShellTargetQueryHandler(_vms.Object, currentUser),
            new RequestVmConsoleCommandHandler(_vms.Object, currentUser, _activity),
            _limiter,
            new IncusShellConnector(_clusters, _factory),
            TimeProvider.System,
            NullLogger<OpenVmConsoleEndpoint>.Instance);
        return (endpoint, db);
    }

    private async Task<WebSocketReceiveResult> ClosedAsync()
    {
        var buffer = new byte[1024];
        while (true)
        {
            WebSocketReceiveResult received = await _customer!.ReceiveAsync(buffer, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return received;
            }
        }
    }

    private async Task<int> FreePlacesAsync()
    {
        var places = new List<IAsyncDisposable>();
        while (places.Count <= ShellSessionLimiter.MaxSessionsPerTenant)
        {
            IAsyncDisposable? place = await _limiter.TryAcquireAsync(Owner.Id, Guid.NewGuid(), CancellationToken.None);
            if (place == null)
            {
                break;
            }
            places.Add(place);
        }
        foreach (IAsyncDisposable place in places)
        {
            await place.DisposeAsync();
        }
        return places.Count;
    }

    private async Task<(int Code, string? Reason)> RefusedAfterTheUpgradeAsync()
    {
        (OpenVmConsoleEndpoint endpoint, _) = Endpoint(Ticket);
        Task handled = endpoint.HandleAsync(CancellationToken.None);
        WebSocketReceiveResult closed = await ClosedAsync().WaitAsync(RealWait);
        await handled.WaitAsync(RealWait);
        return ((int)closed.CloseStatus!, closed.CloseStatusDescription);
    }

    [Fact]
    public async Task APlainGet_IsWebSocketRequired_AndLeavesTheTicketUsable()
    {
        (OpenVmConsoleEndpoint endpoint, TitanDbContext db) = Endpoint(Ticket, upgrade: false);

        var refused = await Assert.ThrowsAsync<BadRequestException>(() => endpoint.HandleAsync(CancellationToken.None));

        Assert.Equal("WEBSOCKET_REQUIRED", refused.Code);
        Assert.Single(await db.ConsoleTickets.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-ticket")]
    [InlineData("ovct_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task AMissingOrUnknownTicket_Is401_BeforeTheUpgrade(string? ticket)
    {
        (OpenVmConsoleEndpoint endpoint, _) = Endpoint(ticket);

        await endpoint.HandleAsync(CancellationToken.None).WaitAsync(RealWait);

        Assert.Equal(StatusCodes.Status401Unauthorized, endpoint.HttpContext.Response.StatusCode);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task ATicketUsedOnce_Is401_TheSecondTime()
    {
        _incus!.Behaviour = FakeIncusConsoleServer.Mode.EndsAfterPrompt;
        (OpenVmConsoleEndpoint first, _) = Endpoint(Ticket);
        Task handled = first.HandleAsync(CancellationToken.None);
        await ClosedAsync().WaitAsync(RealWait);
        await _customer!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await handled.WaitAsync(RealWait);

        (OpenVmConsoleEndpoint second, _) = Endpoint(Ticket);
        await second.HandleAsync(CancellationToken.None).WaitAsync(RealWait);

        Assert.Equal(StatusCodes.Status401Unauthorized, second.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ATicketOfAnotherVm_Is401()
    {
        const string other = "ovct_ccccccccccccccccccccccccccccccccccccccccccc";
        await IssueAsync(other, Guid.NewGuid());
        (OpenVmConsoleEndpoint endpoint, _) = Endpoint(other);

        await endpoint.HandleAsync(CancellationToken.None).WaitAsync(RealWait);

        Assert.Equal(StatusCodes.Status401Unauthorized, endpoint.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ATicketOfSomeoneNoLongerInTheTenant_Is401()
    {
        _tenants.Setup(r => r.FindTenantByMemberAsync("kc-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((Guid, TenantStatus)?)null);
        (OpenVmConsoleEndpoint endpoint, _) = Endpoint(Ticket);

        await endpoint.HandleAsync(CancellationToken.None).WaitAsync(RealWait);

        Assert.Equal(StatusCodes.Status401Unauthorized, endpoint.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ATenantSuspendedSinceTheTicket_IsClosed4403()
    {
        _tenants.Setup(r => r.FindTenantByMemberAsync("kc-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Owner.Id, TenantStatus.Suspended));

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4403, code);
        Assert.Equal("TENANT_SUSPENDED", reason);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task ATenantPendingApproval_IsClosed4403()
    {
        _tenants.Setup(r => r.FindTenantByMemberAsync("kc-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Owner.Id, TenantStatus.PendingApproval));

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4403, code);
        Assert.Equal("TENANT_PENDING_APPROVAL", reason);
        Assert.Empty(_activity.Events);
    }

    // The VM was deleted after the ticket: the guard of every route answers it.
    [Fact]
    public async Task AVmGoneSinceTheTicket_IsClosed4404()
    {
        _vms.Setup(r => r.FindByIdAsync(_vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync((Vm?)null);

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4404, code);
        Assert.Equal("VM_NOT_FOUND", reason);
    }

    [Fact]
    public async Task AVmStoppedSinceTheTicket_IsClosed4422_AndTakesNoPlace()
    {
        _vm.MarkObserved(VmStatus.Stopped, null);

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4422, code);
        Assert.Equal("VM_INVALID_STATUS", reason);
        Assert.Equal(ShellSessionLimiter.MaxSessionsPerTenant, await FreePlacesAsync());
    }

    [Fact]
    public async Task ATenantWithFiveSessionsOpen_IsClosed4422()
    {
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(await _limiter.TryAcquireAsync(Owner.Id, Guid.NewGuid(), CancellationToken.None));
        }

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4422, code);
        Assert.Equal("VM_SESSION_LIMIT_REACHED", reason);
        Assert.Empty(_activity.Events);
    }

    // Dev arm64: Incus cannot attach a serial console. Nothing recorded, the place given back.
    [Fact]
    public async Task AConsoleIncusCannotAttach_IsClosed4422Unavailable_RecordsNothing_AndGivesItsPlaceBack()
    {
        _incus!.Behaviour = FakeIncusConsoleServer.Mode.CannotAttach;

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4422, code);
        Assert.Equal("VM_CONSOLE_UNAVAILABLE", reason);
        Assert.Empty(_activity.Events);
        Assert.Equal(ShellSessionLimiter.MaxSessionsPerTenant, await FreePlacesAsync());
    }

    [Fact]
    public async Task AConsoleAlreadyOpen_IsClosed4422Busy()
    {
        _incus!.Behaviour = FakeIncusConsoleServer.Mode.Busy;

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4422, code);
        Assert.Equal("VM_CONSOLE_BUSY", reason);
    }

    // The customer gets the code; the cluster's address stays in the log.
    [Fact]
    public async Task AClusterFailure_IsClosed4503_WithItsCodeOnly()
    {
        _clusters.Clusters.Clear();
        await using var untrusted = await FakeIncusConsoleServer.StartAsync();
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", untrusted.Endpoint, untrusted.Fingerprint));

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4503, code);
        Assert.Equal("CLUSTER_UNTRUSTED", reason);
    }

    [Fact]
    public async Task AClusterNotConfigured_IsClosed4404()
    {
        _clusters.Clusters.Clear();

        (int code, string? reason) = await RefusedAfterTheUpgradeAsync();

        Assert.Equal(4404, code);
        Assert.Equal("CLUSTER_NOT_FOUND", reason);
    }

    // A customer who leaves while the console opens is no failure: nothing is recorded, and no close is sent on a
    // connection already gone.
    [Fact]
    public async Task ACustomerWhoLeavesWhileItOpens_GetsNoClose_AndNothingIsRecorded()
    {
        (OpenVmConsoleEndpoint endpoint, _) = Endpoint(Ticket);
        using var gone = new CancellationTokenSource();
        Task handled = endpoint.HandleAsync(gone.Token);
        // The console was asked for; the control plane now reads its operation, which takes a second.
        var giveUp = DateTime.UtcNow + RealWait;
        while (_incus!.ConsoleRequests.IsEmpty && DateTime.UtcNow < giveUp)
        {
            await Task.Delay(5);
        }
        await gone.CancelAsync();

        await handled.WaitAsync(RealWait);

        Task<WebSocketReceiveResult> received = _customer!.ReceiveAsync(new byte[64], CancellationToken.None);
        Task first = await Task.WhenAny(received, Task.Delay(TimeSpan.FromMilliseconds(500)));
        bool closeSent = first == received && received.IsCompletedSuccessfully
            && received.Result.MessageType == WebSocketMessageType.Close;
        Assert.False(closeSent);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task AnOpenConsole_RunsAsTheTicketsTaker_AndRecordsOneActivity()
    {
        (OpenVmConsoleEndpoint endpoint, TitanDbContext db) = Endpoint(Ticket);
        Task handled = endpoint.HandleAsync(CancellationToken.None);

        var prompt = new byte[FakeIncusConsoleServer.Prompt.Length];
        int read = 0;
        while (read < prompt.Length)
        {
            read += (await _customer!.ReceiveAsync(new ArraySegment<byte>(prompt, read, prompt.Length - read), CancellationToken.None)).Count;
        }
        Assert.Equal(FakeIncusConsoleServer.Prompt, Encoding.ASCII.GetString(prompt));
        FakeActivityRecorder.Entry entry = Assert.Single(_activity.Events);
        Assert.Equal("vm.console_requested", entry.Action);
        var user = new CurrentUser(_accessor);
        Assert.Equal("kc-1", user.KeycloakUserId);
        Assert.Equal(Owner.Id, user.TenantId);
        Assert.Empty(await db.ConsoleTickets.ToListAsync());

        await _customer!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await handled.WaitAsync(RealWait);
    }
}
