using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Endpoints.VolumeSnapshots;

namespace Titan.Tests.Infrastructure;

// An observation body without `exists` would bind as false and record the resource gone; every observation route
// refuses it with 400 before the handler reads anything. Factory.Create skips JSON binding, so a null Exists stands for
// the missing field. The repositories hold nothing: a body that reaches the handler ends in its 404, which is what
// "read the row" looks like here.
public class ObservationExistsRequiredTests
{
    private const string Secret = "secret";

    public static readonly TheoryData<string> Routes =
        ["Vm", "Volume", "VolumeSnapshot", "VmSnapshot", "Network", "FirewallRule", "LoadBalancer", "Template"];

    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<IVolumeRepository> _volumes = new();
    private readonly Mock<INetworkRepository> _networks = new();
    private readonly Mock<ILoadBalancerRepository> _loadBalancers = new();
    private readonly Mock<IVmTemplateRepository> _templates = new();

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = Secret })
            .Build();

    private static Action<HttpContext> Route(params string[] names) => ctx =>
    {
        ctx.Request.Headers["X-Internal-Token"] = Secret;
        foreach (string name in names)
        {
            ctx.Request.RouteValues[name] = name == "clusterId" ? "mock" : Guid.NewGuid().ToString();
        }
    };

    // Sends the body to the route and answers the status it wrote.
    private async Task<int> Send(string route, bool? exists)
    {
        IActivityRecorder activity = Mock.Of<IActivityRecorder>();
        switch (route)
        {
            case "Vm":
            {
                var ep = Factory.Create<ObserveVmEndpoint>(Route("vmId"),
                    new ObserveVmCommandHandler(_vms.Object), Config());
                await ep.HandleAsync(new ObserveVmBody(exists, exists == true ? new VmObservedBody("Running") : null), default);
                return ep.HttpContext.Response.StatusCode;
            }
            case "Volume":
            {
                var ep = Factory.Create<ObserveVolumeEndpoint>(Route("volumeId"),
                    new ObserveVolumeCommandHandler(_volumes.Object, _vms.Object), Config());
                await ep.HandleAsync(new ObserveVolumeBody(exists), default);
                return ep.HttpContext.Response.StatusCode;
            }
            case "VolumeSnapshot":
            {
                var ep = Factory.Create<ObserveVolumeSnapshotEndpoint>(Route("volumeId", "snapshotId"),
                    new ObserveVolumeSnapshotCommandHandler(_volumes.Object), Config());
                await ep.HandleAsync(new ObserveVolumeSnapshotBody(exists), default);
                return ep.HttpContext.Response.StatusCode;
            }
            case "VmSnapshot":
            {
                var ep = Factory.Create<ObserveVmSnapshotEndpoint>(Route("vmId", "snapshotId"),
                    new ObserveVmSnapshotCommandHandler(_vms.Object), Config());
                await ep.HandleAsync(new ObserveVmSnapshotBody(exists), default);
                return ep.HttpContext.Response.StatusCode;
            }
            case "Network":
            {
                var ep = Factory.Create<ObserveNetworkEndpoint>(Route("networkId"),
                    new ObserveNetworkCommandHandler(_networks.Object), Config());
                await ep.HandleAsync(new ObserveNetworkBody(exists, new NetworkObservedBody("Created")), default);
                return ep.HttpContext.Response.StatusCode;
            }
            case "FirewallRule":
            {
                var ep = Factory.Create<ObserveFirewallRuleEndpoint>(Route("networkId", "ruleId"),
                    new ObserveFirewallRuleCommandHandler(_networks.Object), Config());
                await ep.HandleAsync(new ObserveFirewallRuleBody(exists, new FirewallRuleObservedBody("enabled")), default);
                return ep.HttpContext.Response.StatusCode;
            }
            case "LoadBalancer":
            {
                var ep = Factory.Create<ObserveLoadBalancerEndpoint>(Route("loadBalancerId"),
                    new ObserveLoadBalancerCommandHandler(_loadBalancers.Object), Config());
                await ep.HandleAsync(new ObserveLoadBalancerBody(exists), default);
                return ep.HttpContext.Response.StatusCode;
            }
            default:
            {
                ClusterOptions clusters = new() { Clusters = [new ClusterConfig { Id = "mock", Name = "Mock" }] };
                var ep = Factory.Create<ObserveTemplateEndpoint>(Route("templateId", "clusterId"),
                    new ObserveTemplateCommandHandler(_templates.Object, clusters), Config());
                await ep.HandleAsync(new ObserveTemplateBody(exists), default);
                return ep.HttpContext.Response.StatusCode;
            }
        }
    }

    private void NothingRead()
    {
        _vms.VerifyNoOtherCalls();
        _volumes.VerifyNoOtherCalls();
        _networks.VerifyNoOtherCalls();
        _loadBalancers.VerifyNoOtherCalls();
        _templates.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ABodyWithoutExists_Is400_AndTheHandlerReadsNothing(string route)
    {
        int status = await Send(route, exists: null);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        NothingRead();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ABodyWithExistsTrueOrFalse_ReachesTheHandler(string route)
    {
        foreach (bool exists in new[] { true, false })
        {
            await Assert.ThrowsAsync<NotFoundException>(() => Send(route, exists));
        }
    }
}
