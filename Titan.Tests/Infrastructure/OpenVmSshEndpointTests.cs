using System.Net.WebSockets;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Options;
using Titan.Application.Queries.Compute.Vm;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Vm;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;
using Titan.Tests.Fakes;
using Titan.Tests.Infrastructure.Incus;
using Titan.Application.Exceptions;

namespace Titan.Tests.Infrastructure;

// The SSH endpoint's own gates (titan-ops features/vm/design.md#ssh-session): a websocket only, the per-tenant session
// limit before anything is recorded or execed, and the order that keeps a refusal free of side effects. Silent about
// authentication and the pipeline: Factory.Create builds the endpoint without a host.
public class OpenVmSshEndpointTests
{
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly ShellSessionLimiter _limiter = NewLimiter();
    private readonly ClusterOptions _clusters = new();
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant _tenantIdOwner = TestTenant.WithRoomToSpare();
    private readonly Guid _tenantId = _tenantIdOwner.Id;
    private readonly Vm _vm;

    private static ShellSessionLimiter NewLimiter()
    {
        string database = $"ssh-endpoint-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<TitanDbContext>(o => o.UseInMemoryDatabase(database));
        return new ShellSessionLimiter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<ShellSessionLimiter>.Instance);
    }

    public OpenVmSshEndpointTests()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(_tenantId);
        _vm = Vm.Create(_tenantIdOwner.Id, Guid.NewGuid(), "web-1", "s", "tpl-1", "local-mac", 20);
        _vm.MarkCreated("vmabc");
        _vm.MarkObserved(VmStatus.Running, "10.0.0.5");
        _vms.Setup(r => r.FindByIdAsync(_vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_vm);
    }

    private sealed class UpgradeRequest : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    private OpenVmSshEndpoint Endpoint(bool upgrade, IncusClientFactory? factory = null, TimeProvider? clock = null) =>
        Factory.Create<OpenVmSshEndpoint>(ctx =>
            {
                ctx.Request.RouteValues["vmId"] = _vm.Id.ToString();
                if (upgrade)
                {
                    ctx.Features.Set<IHttpWebSocketFeature>(new UpgradeRequest());
                }
            },
            new GetVmShellTargetQueryHandler(_vms.Object, _currentUser.Object),
            new RequestVmSshCommandHandler(_vms.Object, _currentUser.Object, _activity),
            _currentUser.Object,
            _limiter,
            new IncusShellConnector(_clusters, factory ?? new IncusClientFactory(IncusTestKit.IdentityStore())),
            clock ?? TimeProvider.System,
            NullLogger<OpenVmSshEndpoint>.Instance);

    [Fact]
    public async Task APlainGet_IsWebSocketRequired_AndRecordsNothing()
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() => Endpoint(upgrade: false).HandleAsync(CancellationToken.None));

        Assert.Equal("WEBSOCKET_REQUIRED", refused.Code);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task ATenantWithFiveSessionsOpen_IsRefusedTheSixth_BeforeAnythingIsRecorded()
    {
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(await _limiter.TryAcquireAsync(_tenantId, Guid.NewGuid(), CancellationToken.None));
        }

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Endpoint(upgrade: true).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_SESSION_LIMIT_REACHED", refused.Code);
        Assert.Empty(_activity.Events);
    }

    // The place taken for a session is given back when opening the exec fails, or a VM that is still starting would
    // use up the tenant's five places in five tries. A session that never opened records nothing.
    [Fact]
    public async Task AFailedOpen_GivesItsPlaceBack_AndRecordsNothing()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        await using var incus = await FakeIncusExecServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = FakeIncusExecServer.Mode.AgentNotRunning;
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            Endpoint(upgrade: true, new IncusClientFactory(store)).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_AGENT_NOT_READY", refused.Code);
        Assert.Empty(_activity.Events);
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(await _limiter.TryAcquireAsync(_tenantId, Guid.NewGuid(), CancellationToken.None));
        }
    }

    // The agent is up but sshd is not listening (cloud-init still runs, or sshd is stopped): nc ends at once without the
    // server's banner, before a client that connects control last gets to it (FakeIncusExecServer). The
    // customer gets a code to retry on, before the upgrade; nothing is recorded and the place is given back.
    [Fact]
    public async Task AVmWithNothingListeningOnPort22_IsVmSshNotReady_RecordsNothing_AndGivesItsPlaceBack()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        await using var incus = await FakeIncusExecServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = FakeIncusExecServer.Mode.EndsWithoutOutput;
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            Endpoint(upgrade: true, new IncusClientFactory(store)).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_SSH_NOT_READY", refused.Code);
        Assert.Empty(_activity.Events);
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(await _limiter.TryAcquireAsync(_tenantId, Guid.NewGuid(), CancellationToken.None));
        }
    }

    // nc that ends without a byte several seconds in, within BannerWait, is still a refusal to retry on, not a session
    // that opens and closes empty.
    [Fact]
    public async Task ACommandThatEndsWithoutAByteSixSecondsIn_IsStillVmSshNotReady_RecordsNothing_AndGivesItsPlaceBack()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        await using var incus = await FakeIncusExecServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = FakeIncusExecServer.Mode.StallsThenEndsWithoutOutput;
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));
        var clock = new FakeTimeProvider();

        Task handled = Endpoint(upgrade: true, new IncusClientFactory(store), clock).HandleAsync(CancellationToken.None);
        await incus.CommandStarted.Task.WaitAsync(RealWait);
        await AdvanceAsync(clock, TimeSpan.FromSeconds(6));
        incus.EndStalledCommand();

        var refused = await Assert.ThrowsAsync<BusinessException>(() => handled.WaitAsync(RealWait));
        Assert.Equal("VM_SSH_NOT_READY", refused.Code);
        Assert.Empty(_activity.Events);
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(await _limiter.TryAcquireAsync(_tenantId, Guid.NewGuid(), CancellationToken.None));
        }
    }

    // A command still silent when BannerWait is over is not refused: the session opens (here the upgrade itself, which
    // this rig does not serve) and is recorded, at 30 s (one more step of the fake clock is tolerated).
    [Fact]
    public async Task ACommandSilentForTheWholeBannerWait_OpensTheSession_AfterThirtySeconds()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        await using var incus = await FakeIncusExecServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = FakeIncusExecServer.Mode.Silent;
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));
        var clock = new FakeTimeProvider();

        Task handled = Endpoint(upgrade: true, new IncusClientFactory(store), clock).HandleAsync(CancellationToken.None);
        await incus.CommandStarted.Task.WaitAsync(RealWait);
        await AdvanceAsync(clock, TimeSpan.FromSeconds(29));
        Assert.False(handled.IsCompleted);
        Assert.Empty(_activity.Events);

        int more = 0;
        while (!handled.IsCompleted && more < 10)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            more++;
            await Task.Delay(200);
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => handled.WaitAsync(RealWait));
        Assert.InRange(more, 1, 2);
        Assert.Single(_activity.Events);
    }

    private static readonly TimeSpan RealWait = TimeSpan.FromSeconds(20);

    // Moves the clock a second at a time with a real pause before and between the steps, so the endpoint's delay,
    // which starts just after the command, is most likely running before the first step.
    private static async Task AdvanceAsync(FakeTimeProvider clock, TimeSpan by)
    {
        await Task.Delay(100);
        for (var moved = TimeSpan.Zero; moved < by; moved += TimeSpan.FromSeconds(1))
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }
    }

    // The customer gets the code; the cluster's address, the tenant's project and the operation stay in the log, as
    // in the catalog.
    [Fact]
    public async Task AClusterFailure_KeepsItsCode_AndHidesItsDetail()
    {
        await using var incus = await FakeIncusExecServer.StartAsync();
        _clusters.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));

        var refused = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Endpoint(upgrade: true).HandleAsync(CancellationToken.None));

        Assert.Equal("CLUSTER_UNTRUSTED", refused.Code);
        Assert.DoesNotContain(incus.Endpoint.Replace("https://", ""), refused.Message);
        Assert.DoesNotContain("fingerprint", refused.Message);
    }

    [Fact]
    public async Task AStoppedVm_IsInvalidStatus_AndTakesNoPlace()
    {
        _vm.MarkObserved(VmStatus.Stopped, null);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Endpoint(upgrade: true).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Empty(_activity.Events);
    }
}
