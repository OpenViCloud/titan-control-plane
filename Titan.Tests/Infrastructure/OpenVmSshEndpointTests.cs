using System.Net.WebSockets;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Options;
using Titan.Application.Queries.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Infrastructure.Endpoints.Vm;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Sessions;
using Titan.Tests.Fakes;
using Titan.Tests.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure;

// The SSH endpoint's own gates (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session): a websocket only, the per-tenant session
// limit before anything is recorded or execed, and the order that keeps a refusal free of side effects. Silent about
// authentication and the pipeline: Factory.Create builds the endpoint without a host.
public class OpenVmSshEndpointTests
{
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly ShellSessionLimiter _limiter = new();
    private readonly ZoneOptions _clusters = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Vm _vm;

    public OpenVmSshEndpointTests()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(_tenantId);
        _vm = Vm.CreateUnchecked(_tenantId, Guid.NewGuid(), "web-1", "s", "tpl-1", "local-mac", 20);
        _vm.MarkCreated("vmabc");
        _vm.MarkObserved(VmState.Running, "10.0.0.5");
        _vms.Setup(r => r.FindByIdAsync(_vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_vm);
    }

    private sealed class UpgradeRequest : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    private OpenVmSshEndpoint Endpoint(bool upgrade, IncusClientFactory? factory = null) =>
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
            Assert.NotNull(_limiter.TryAcquire(_tenantId));
        }

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Endpoint(upgrade: true).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_SESSION_LIMIT_REACHED", refused.Code);
        Assert.Empty(_activity.Events);
    }

    // The place taken for a session is given back when opening the exec fails, or a VM that is still starting would
    // use up the tenant's five places in five tries.
    [Fact]
    public async Task AFailedOpen_GivesItsPlaceBack()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        await using var incus = await FakeIncusExecServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = FakeIncusExecServer.Mode.AgentNotRunning;
        _clusters.Zones.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            Endpoint(upgrade: true, new IncusClientFactory(store)).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_AGENT_NOT_READY", refused.Code);
        Assert.Equal("vm.ssh_requested", Assert.Single(_activity.Events).Action);
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(_limiter.TryAcquire(_tenantId));
        }
    }

    // The customer gets the code; the cluster's address, the tenant's project and the operation stay in the log, as
    // in the catalog.
    [Fact]
    public async Task AClusterFailure_KeepsItsCode_AndHidesItsDetail()
    {
        await using var incus = await FakeIncusExecServer.StartAsync();
        _clusters.Zones.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));

        var refused = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Endpoint(upgrade: true).HandleAsync(CancellationToken.None));

        Assert.Equal("CLUSTER_UNTRUSTED", refused.Code);
        Assert.DoesNotContain(incus.Endpoint.Replace("https://", ""), refused.Message);
        Assert.DoesNotContain("fingerprint", refused.Message);
    }

    [Fact]
    public async Task AStoppedVm_IsInvalidStatus_AndTakesNoPlace()
    {
        _vm.MarkObserved(VmState.Stopped, null);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Endpoint(upgrade: true).HandleAsync(CancellationToken.None));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Empty(_activity.Events);
    }
}
