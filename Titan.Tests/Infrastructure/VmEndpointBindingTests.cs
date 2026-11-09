using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Vm;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The wiring between the 7 VM endpoints and the handlers behind them: "this endpoint reaches that one
// operation, carrying these arguments, and returns what came back". The VM handler tests call the
// handlers directly, so nothing else observes whether an endpoint calls the right operation with the
// right arguments.
//
// What these DO measure: route-to-handler binding, argument pass-through, status code, response body.
// What they are SILENT about: authorization, validation, serialization, and everything else the real
// HTTP pipeline does — Factory.Create builds an endpoint without a host. A green run here is not
// "the VM API is tested", and saying so is the point.
public class VmEndpointBindingTests
{
    private static readonly Guid VmId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;

    // Handlers are concrete classes with no interface (titan-ops architecture/code-structure.md#no-mediatr), so the recording happens one
    // layer down, at the repository. That is a better place for it anyway: it proves the endpoint
    // reached a handler that really did the work, not merely that some object was invoked.
    private readonly Mock<IVmRepository> _repo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    public VmEndpointBindingTests()
    {
        _currentUser.SetupGet(u => u.TenantId).Returns(TenantId);
        _repo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(() => SampleVm());
        // Every lifecycle handler locks the row (titan-ops architecture/resource-lifecycle.md#one-key-per-request); a mock has
        // no rows, so the locked read answers what the plain one does.
        _repo.Setup(r => r.FindByIdForUpdateAsync(VmId, default)).Returns(() => _repo.Object.FindByIdAsync(VmId, default));
        _repo.Setup(r => r.ListByTenantAsync(TenantId, default)).ReturnsAsync(() => [SampleVm()]);
    }

    private static Titan.Domain.Aggregates.Tenant.Tenant ActiveTenant() =>
        TestTenant.Active("acme", "owner-1", "admin@acme.com");

    // A VM owned by TenantId and already Running, so the ownership check passes and Stop has a
    // legal state to hand an external id out of.
    private static VmAggregate SampleVm()
    {
        // The VM has to belong to THIS TenantId, the one _currentUser reports.
        var vm = VmAggregate.Create(TenantIdOwner.Id, Guid.NewGuid(), "web-01", "offering-1", Guid.NewGuid().ToString(), "cluster-1", 20);
        vm.MarkRunning("one-42", "10.0.0.5", null);
        return vm;
    }

    // Route values are where the endpoint reads vmId from; Factory.Create does not run routing, so
    // the test supplies them exactly as the router would.
    private static void WithVmIdRoute(HttpContext ctx) =>
        ctx.Request.RouteValues["vmId"] = VmId.ToString();

    private ListVmsQueryHandler ListHandler() => new(_repo.Object, _currentUser.Object);
    private GetVmQueryHandler GetHandler() => new(_repo.Object, _currentUser.Object);
    private RenameVmCommandHandler RenameHandler() => new(_repo.Object, _currentUser.Object);

    [Fact] // GET /v1/tenant/vms — reaches the LIST query, not the single-item one
    public async Task ListVmsEndpoint_ReachesListQuery_AndReturnsWhatItGot()
    {
        var ep = Factory.Create<ListVmsEndpoint>(ListHandler());

        await ep.HandleAsync(CancellationToken.None);

        _repo.Verify(r => r.ListByTenantAsync(TenantId, default), Times.Once);
        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), default), Times.Never);
        Assert.Single(ep.Response);
        Assert.Equal("web-01", ep.Response[0].Name);
    }

    [Fact] // GET /v1/tenant/vms/{vmId} — reaches the single-item query WITH the route id
    public async Task GetVmEndpoint_ReachesGetQuery_WithRouteVmId()
    {
        var ep = Factory.Create<GetVmEndpoint>(WithVmIdRoute, GetHandler());

        await ep.HandleAsync(CancellationToken.None);

        _repo.Verify(r => r.FindByIdAsync(VmId, default), Times.Once);
        _repo.Verify(r => r.ListByTenantAsync(It.IsAny<Guid>(), default), Times.Never);
        Assert.Equal("web-01", ep.Response.Name);
    }

    [Fact] // POST /v1/tenant/vms — 202, and the request body reaches the handler unchanged
    public async Task CreateVmEndpoint_ReachesCreateCommand_WithBody_And202()
    {
        var tenantRepo = new Mock<Titan.Domain.Repositories.ITenantRepository>();
        tenantRepo.Setup(r => r.FindByIdAsync(TenantId, default))
            .ReturnsAsync(ActiveTenant());
        var handler = new CreateVmCommandHandler(
            _repo.Object,
            new Mock<Titan.Domain.Repositories.INetworkRepository>().Object,
            tenantRepo.Object, _currentUser.Object,
            new Mock<IProviderCommandQueue>().Object,
            new Mock<Titan.Domain.Repositories.IVmOfferingRepository>().Object,
            new Mock<Titan.Domain.Repositories.IVmTemplateRepository>().Object,
            new Titan.Tests.Fakes.FakeSshKeyRepository(),
            new Titan.Tests.Fakes.FakeActivityRecorder(),
            new Titan.Application.Options.ClusterOptions { Clusters = [new() { Id = "cluster-1", Name = "Cluster 1" }] });
        var ep = Factory.Create<CreateVmEndpoint>(handler);
        var req = new CreateVmCommand(
            "new-vm", Guid.NewGuid(), "offering-1", Guid.NewGuid().ToString(), "cluster-1", 40, null);

        // The create path refuses before it reaches the provider (no network is registered on the
        // stub), and that is fine: what this test measures is that the endpoint handed the body to
        // the CREATE handler at all. It reached the name check, so the body arrived.
        //
        // The exception TYPE is asserted rather than any exception at all, which would also be
        // satisfied by a NullReferenceException from a badly built stub — a test that passes when the
        // fixture is broken is not measuring the endpoint.
        var ex = await Assert.ThrowsAsync<Titan.Domain.Exceptions.NotFoundException>(
            () => ep.HandleAsync(req, CancellationToken.None));
        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);

        _repo.Verify(r => r.ExistsByNameAsync(TenantId, "new-vm", default), Times.Once);
    }

    [Fact] // POST /v1/tenant/vms without clusterId — the body binds and reaches the handler (the network is then looked up)
    public async Task CreateVmEndpoint_WithoutClusterId_ReachesTheHandler()
    {
        var tenantRepo = new Mock<Titan.Domain.Repositories.ITenantRepository>();
        tenantRepo.Setup(r => r.FindByIdAsync(TenantId, default)).ReturnsAsync(ActiveTenant());
        var handler = new CreateVmCommandHandler(
            _repo.Object,
            new Mock<Titan.Domain.Repositories.INetworkRepository>().Object,
            tenantRepo.Object, _currentUser.Object,
            new Mock<IProviderCommandQueue>().Object,
            new Mock<Titan.Domain.Repositories.IVmOfferingRepository>().Object,
            new Mock<Titan.Domain.Repositories.IVmTemplateRepository>().Object,
            new Titan.Tests.Fakes.FakeSshKeyRepository(),
            new Titan.Tests.Fakes.FakeActivityRecorder(),
            new Titan.Application.Options.ClusterOptions { Clusters = [new() { Id = "cluster-1", Name = "Cluster 1" }] });
        var ep = Factory.Create<CreateVmEndpoint>(handler);
        var req = new CreateVmCommand("new-vm", Guid.NewGuid(), "offering-1", Guid.NewGuid().ToString(), null, 40, null);

        var ex = await Assert.ThrowsAsync<Titan.Domain.Exceptions.NotFoundException>(
            () => ep.HandleAsync(req, CancellationToken.None));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code); // past the name check and the (skipped) cluster check: the body arrived
        _repo.Verify(r => r.ExistsByNameAsync(TenantId, "new-vm", default), Times.Once);
    }

    // PATCH /v1/tenant/vms/{vmId} — rename carries BOTH the route id and the body, and answers 204
    // with no body (titan-ops architecture/http-contract.md#commands-return-no-data). The command side owns no read
    // shape, so a caller that wants the renamed VM reads it back from the query side.
    [Fact]
    public async Task UpdateVmEndpoint_ReachesRenameCommand_WithRouteVmIdAndBody_And204()
    {
        var ep = Factory.Create<UpdateVmEndpoint>(WithVmIdRoute, RenameHandler());

        await ep.HandleAsync(new RenameVmBody("renamed-vm"), CancellationToken.None);

        _repo.Verify(r => r.FindByIdAsync(VmId, default), Times.Once);
        _repo.Verify(r => r.ExistsByNameAsync(TenantId, "renamed-vm", default), Times.Once);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        Assert.Equal(204, ep.HttpContext.Response.StatusCode);
    }

    [Fact] // DELETE /v1/tenant/vms/{vmId}
    public async Task DeleteVmEndpoint_ReachesDeleteCommand_WithRouteVmId_And202()
    {
        var commands = new Mock<IProviderCommandQueue>();
        var volumes = new Mock<IVolumeRepository>();
        volumes.Setup(v => v.ListAttachedToVmLockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Titan.Domain.Aggregates.Volume.Volume>());
        var handler = new DeleteVmCommandHandler(
            _repo.Object, _currentUser.Object, commands.Object,
            volumes.Object, new Titan.Tests.Fakes.FakeActivityRecorder(),
            new Titan.Domain.Services.VmDeletion(new Mock<ILoadBalancerRepository>().Object));
        // The delete reads the VM with its row locked (titan-ops architecture/domain-services.md#cross-aggregate-lock).
        _repo.Setup(r => r.FindByIdForUpdateAsync(VmId, default)).Returns(() => _repo.Object.FindByIdAsync(VmId, default));
        var ep = Factory.Create<DeleteVmEndpoint>(WithVmIdRoute, handler);

        await ep.HandleAsync(CancellationToken.None);

        _repo.Verify(r => r.FindByIdForUpdateAsync(VmId, default), Times.Once);
        commands.Verify(w => w.EnqueueVmDeleteAsync(It.IsAny<VmAggregate>(), It.IsAny<Guid>(), "one-42", It.IsAny<string>(), default), Times.Once);
        Assert.Equal(202, ep.HttpContext.Response.StatusCode);
    }

    // Stop and Start are the pair most at risk of being crossed: same shape, same route prefix, one
    // word apart. Asserting each reaches ITS OWN enqueue call is the whole reason both tests exist.
    [Fact] // POST /v1/tenant/vms/{vmId}/stop
    public async Task StopVmEndpoint_ReachesStopCommand_WithRouteVmId_And202()
    {
        var commands = new Mock<IProviderCommandQueue>();
        var handler = new StopVmCommandHandler(
            _repo.Object, _currentUser.Object, commands.Object, new Titan.Tests.Fakes.FakeActivityRecorder());
        var ep = Factory.Create<StopVmEndpoint>(WithVmIdRoute, handler);

        await ep.HandleAsync(CancellationToken.None);

        commands.Verify(w => w.EnqueueVmStopAsync(It.IsAny<VmAggregate>(), It.IsAny<Guid>(), "one-42", It.IsAny<string>(), default), Times.Once);
        commands.Verify(w => w.EnqueueVmStartAsync(It.IsAny<VmAggregate>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        Assert.Equal(202, ep.HttpContext.Response.StatusCode);
    }

    [Fact] // POST /v1/tenant/vms/{vmId}/start
    public async Task StartVmEndpoint_ReachesStartCommand_WithRouteVmId_And202()
    {
        // A stopped VM, because Start dispatches only from Stopped — the mirror of the Stop test.
        var stopped = SampleVm();
        stopped.MarkStopped();
        _repo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(stopped);

        var commands = new Mock<IProviderCommandQueue>();
        var handler = new StartVmCommandHandler(
            _repo.Object, _currentUser.Object, commands.Object, new Titan.Tests.Fakes.FakeActivityRecorder());
        var ep = Factory.Create<StartVmEndpoint>(WithVmIdRoute, handler);

        await ep.HandleAsync(CancellationToken.None);

        commands.Verify(w => w.EnqueueVmStartAsync(It.IsAny<VmAggregate>(), It.IsAny<Guid>(), "one-42", It.IsAny<string>(), default), Times.Once);
        commands.Verify(w => w.EnqueueVmStopAsync(It.IsAny<VmAggregate>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        Assert.Equal(202, ep.HttpContext.Response.StatusCode);
    }
}
