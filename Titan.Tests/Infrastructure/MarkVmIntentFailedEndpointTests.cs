using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Repositories.Compute;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Tests.Infrastructure;

// The worker's "stop/start gave up" callback. It carries its own logic (the internal token gate and two
// route ids), so it gets its own test. The route ids are the risk: swapping them would hand the handler a
// VM id as the intent id, and the report would silently clear nothing.
public class MarkVmIntentFailedEndpointTests
{
    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static Vm RunningVmWaitingForAStop(out Guid intentId)
    {
        Vm vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        intentId = vm.RequestStop().IntentId;
        return vm;
    }

    private static MarkVmIntentFailedEndpoint EndpointFor(Vm vm, Guid intentId, string? token)
    {
        Mock<IVmRepository> repo = new();
        repo.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);

        return Factory.Create<MarkVmIntentFailedEndpoint>(
            ctx =>
            {
                if (token != null)
                {
                    ctx.Request.Headers["X-Internal-Token"] = token;
                }
                ctx.Request.RouteValues["vmId"] = vm.Id.ToString();
                ctx.Request.RouteValues["intentId"] = intentId.ToString();
            },
            new MarkVmIntentFailedCommandHandler(repo.Object, Mock.Of<IActivityRecorder>()),
            ConfigWithToken("secret"));
    }

    [Fact]
    public async Task WithTheToken_ClearsTheNamedIntent_AndAnswers204()
    {
        Vm vm = RunningVmWaitingForAStop(out Guid intentId);
        MarkVmIntentFailedEndpoint ep = EndpointFor(vm, intentId, "secret");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Null(vm.PendingIntent);
        Assert.Equal(StatusCodes.Status204NoContent, ep.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheToken_Answers401_AndChangesNothing()
    {
        Vm vm = RunningVmWaitingForAStop(out Guid intentId);
        MarkVmIntentFailedEndpoint ep = EndpointFor(vm, intentId, null);

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(VmIntent.Stop, vm.PendingIntent);
        Assert.Equal(StatusCodes.Status401Unauthorized, ep.HttpContext.Response.StatusCode);
    }
}
