using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Tests.Infrastructure;

// The two request routes (titan-ops architecture/resource-lifecycle.md#callback-routes) carry their own logic: the internal
// token gate and, for the report, two route ids. Swapping the ids would hand the handler a VM id as the request id, and
// the report would silently end nothing — so the route ids are the risk these tests pin.
public class VmRequestEndpointTests
{
    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static Vm RunningVmWaitingForAStop(out Guid requestId)
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        requestId = Guid.NewGuid();
        vm.Stop(requestId);
        return vm;
    }

    private static Mock<IVmRepository> RepoHolding(Vm vm)
    {
        Mock<IVmRepository> repo = new();
        repo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return repo;
    }

    private static EndVmRequestEndpoint EndEndpoint(Vm vm, Guid requestId, string? token) =>
        Factory.Create<EndVmRequestEndpoint>(
            ctx =>
            {
                if (token != null) ctx.Request.Headers["X-Internal-Token"] = token;
                ctx.Request.RouteValues["vmId"] = vm.Id.ToString();
                ctx.Request.RouteValues["requestId"] = requestId.ToString();
            },
            new EndVmRequestCommandHandler(RepoHolding(vm).Object, Mock.Of<IVolumeRepository>(), Mock.Of<IActivityRecorder>()),
            ConfigWithToken("secret"));

    private static ObserveVmEndpoint ObserveEndpoint(Vm vm, string? token) =>
        Factory.Create<ObserveVmEndpoint>(
            ctx =>
            {
                if (token != null) ctx.Request.Headers["X-Internal-Token"] = token;
                ctx.Request.RouteValues["vmId"] = vm.Id.ToString();
            },
            new ObserveVmCommandHandler(RepoHolding(vm).Object),
            ConfigWithToken("secret"));

    [Fact]
    public async Task Report_WithTheToken_EndsTheNamedRequest_AndAnswers204()
    {
        Vm vm = RunningVmWaitingForAStop(out Guid requestId);
        EndVmRequestEndpoint ep = EndEndpoint(vm, requestId, "secret");

        await ep.HandleAsync(new EndVmRequestBody("Done", null, new VmObservedBody("Stopped")), CancellationToken.None);

        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopped, vm.ObservedState);
        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Equal(StatusCodes.Status204NoContent, ep.HttpContext.Response.StatusCode);
    }

    [Fact] // the worker has nothing to retry: a report the VM is not waiting on is 204 too
    public async Task Report_NamingAnotherRequest_ChangesNothing_AndStillAnswers204()
    {
        Vm vm = RunningVmWaitingForAStop(out Guid requestId);
        EndVmRequestEndpoint ep = EndEndpoint(vm, Guid.NewGuid(), "secret");

        await ep.HandleAsync(new EndVmRequestBody("Failed", Reason: "stale"), CancellationToken.None);

        Assert.Equal(requestId, vm.PendingOperationId!.Value);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Null(vm.OperationFailureReason);
        Assert.Equal(StatusCodes.Status204NoContent, ep.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Report_WithoutTheToken_Answers401_AndChangesNothing()
    {
        Vm vm = RunningVmWaitingForAStop(out Guid requestId);
        EndVmRequestEndpoint ep = EndEndpoint(vm, requestId, null);

        await ep.HandleAsync(new EndVmRequestBody("Done", null, new VmObservedBody("Stopped")), CancellationToken.None);

        Assert.Equal(requestId, vm.PendingOperationId!.Value);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(StatusCodes.Status401Unauthorized, ep.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Observation_WithTheToken_RecordsTheIncusWord_LeavesTheRequest_AndAnswers204()
    {
        Vm vm = RunningVmWaitingForAStop(out Guid requestId);
        ObserveVmEndpoint ep = ObserveEndpoint(vm, "secret");

        await ep.HandleAsync(new ObserveVmBody(true, new VmObservedBody("Frozen", "10.0.0.7")), CancellationToken.None);

        Assert.Equal(requestId, vm.PendingOperationId!.Value);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Frozen, vm.ObservedState);
        Assert.Equal("10.0.0.7", vm.IpAddress);
        Assert.Equal(StatusCodes.Status204NoContent, ep.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Observation_WithoutTheToken_Answers401_AndChangesNothing()
    {
        Vm vm = RunningVmWaitingForAStop(out _);
        ObserveVmEndpoint ep = ObserveEndpoint(vm, null);

        await ep.HandleAsync(new ObserveVmBody(false), CancellationToken.None);

        // A not-found observation would have forgotten the Incus word; the waiting stop keeps the status either way.
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(StatusCodes.Status401Unauthorized, ep.HttpContext.Response.StatusCode);
    }
}
