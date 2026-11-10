using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Options;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Endpoints.VolumeSnapshots;

namespace Titan.Tests.Infrastructure;

// Worker → CP callbacks authenticate with the shared X-Internal-Token. Strict repository mocks make any read or
// write by the handler throw, so a 401 here also proves the request never reached the write path.
//
// Silent about timing: constant-time comparison cannot be observed from a test; the source guard below only
// proves every callback goes through the helper that does it.
public class InternalTokenCallbackTests
{
    private const string Secret = "secret";

    private static IConfiguration ConfigWithToken() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = Secret })
            .Build();

    private static void SetToken(Microsoft.AspNetCore.Http.HttpContext ctx, string? token)
    {
        if (token is not null)
        {
            ctx.Request.Headers["X-Internal-Token"] = token;
        }
    }

    private static IActivityRecorder StrictActivity() => new Mock<IActivityRecorder>(MockBehavior.Strict).Object;

    private static ILoadBalancerRepository StrictLoadBalancers() => new Mock<ILoadBalancerRepository>(MockBehavior.Strict).Object;

    private static IVmTemplateRepository StrictTemplates() => new Mock<IVmTemplateRepository>(MockBehavior.Strict).Object;

    private static readonly ClusterOptions OneCluster = new() { Clusters = [new ClusterConfig { Id = "mock", Name = "Mock" }] };

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndTemplateSyncRequest_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<EndTemplateSyncRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["templateId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["clusterId"] = "mock";
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new EndTemplateSyncRequestCommandHandler(StrictTemplates(), OneCluster), ConfigWithToken());

        await ep.HandleAsync(new EndTemplateSyncRequestBody("Done", ExternalId: "tpl-1"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ObserveTemplate_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<ObserveTemplateEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["templateId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["clusterId"] = "mock";
                SetToken(ctx, token);
            },
            new ObserveTemplateCommandHandler(StrictTemplates(), OneCluster), ConfigWithToken());

        await ep.HandleAsync(new ObserveTemplateBody(false), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndLoadBalancerRequest_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<EndLoadBalancerRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["loadBalancerId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new EndLoadBalancerRequestCommandHandler(StrictLoadBalancers(), StrictActivity()), ConfigWithToken());

        await ep.HandleAsync(new EndLoadBalancerRequestBody("Done", ExternalId: "2001:db8::1:1"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ObserveLoadBalancer_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<ObserveLoadBalancerEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["loadBalancerId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new ObserveLoadBalancerCommandHandler(StrictLoadBalancers()), ConfigWithToken());

        await ep.HandleAsync(new ObserveLoadBalancerBody(false), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndVmSnapshotRequest_WithoutTheToken_Returns401(string? token)
    {
        var handler = new EndVmSnapshotRequestCommandHandler(new Mock<IVmRepository>(MockBehavior.Strict).Object, StrictActivity());
        var ep = Factory.Create<EndVmSnapshotRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["vmId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["snapshotId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new EndVmSnapshotRequestBody("Done", ExternalId: "vm1/s3"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndVolumeRequest_WithoutTheToken_Returns401(string? token)
    {
        var handler = new EndVolumeRequestCommandHandler(new Mock<IVolumeRepository>(MockBehavior.Strict).Object,
            new Mock<IVmRepository>(MockBehavior.Strict).Object,
            StrictActivity());
        var ep = Factory.Create<EndVolumeRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["volumeId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new EndVolumeRequestBody("Done", ExternalId: "vol5"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ObserveVolume_WithoutTheToken_Returns401(string? token)
    {
        var handler = new ObserveVolumeCommandHandler(new Mock<IVolumeRepository>(MockBehavior.Strict).Object,
            new Mock<IVmRepository>(MockBehavior.Strict).Object);
        var ep = Factory.Create<ObserveVolumeEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["volumeId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new ObserveVolumeBody(Exists: true, new VolumeObservedBody(["vm5"])), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndVolumeSnapshotRequest_WithoutTheToken_Returns401(string? token)
    {
        var handler = new EndVolumeSnapshotRequestCommandHandler(new Mock<IVolumeRepository>(MockBehavior.Strict).Object, StrictActivity());
        var ep = Factory.Create<EndVolumeSnapshotRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["volumeId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["snapshotId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new EndVolumeSnapshotRequestBody("Done", ExternalId: "vol5/s9"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ObserveVolumeSnapshot_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<ObserveVolumeSnapshotEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["volumeId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["snapshotId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new ObserveVolumeSnapshotCommandHandler(new Mock<IVolumeRepository>(MockBehavior.Strict).Object), ConfigWithToken());

        await ep.HandleAsync(new ObserveVolumeSnapshotBody(Exists: true), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    private static INetworkRepository StrictNetworks() => new Mock<INetworkRepository>(MockBehavior.Strict).Object;

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndNetworkRequest_WithoutTheToken_Returns401(string? token)
    {
        var handler = new EndNetworkRequestCommandHandler(StrictNetworks(),
            new Titan.Domain.Services.NetworkRoutes(new Mock<IClusterIpv6RouteRepository>(MockBehavior.Strict).Object), StrictActivity());
        var ep = Factory.Create<EndNetworkRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["networkId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new EndNetworkRequestBody("Done", ExternalId: "net-7",
            Observed: new NetworkObservedBody("Created", "10.0.0.0/24", "10.0.0.1")), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ObserveNetwork_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<ObserveNetworkEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["networkId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new ObserveNetworkCommandHandler(StrictNetworks()), ConfigWithToken());

        await ep.HandleAsync(new ObserveNetworkBody(Exists: true, new NetworkObservedBody("Created")), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EndFirewallRuleRequest_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<EndFirewallRuleRequestEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["networkId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["ruleId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["requestId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new EndFirewallRuleRequestCommandHandler(StrictNetworks(), StrictActivity()), ConfigWithToken());

        await ep.HandleAsync(new EndFirewallRuleRequestBody("Done", ExternalId: "acl/rule-1",
            Observed: new FirewallRuleObservedBody("enabled")), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ObserveFirewallRule_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<ObserveFirewallRuleEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["networkId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["ruleId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new ObserveFirewallRuleCommandHandler(StrictNetworks()), ConfigWithToken());

        await ep.HandleAsync(new ObserveFirewallRuleBody(Exists: true, new FirewallRuleObservedBody("enabled")), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    // The token is read before the body's verdict, so a wrong token answers 401 whatever the batch holds.
    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task RecordVmMetrics_WithoutTheToken_Returns401(string? token)
    {
        var ep = Factory.Create<RecordVmMetricsEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["clusterId"] = "mock";
                SetToken(ctx, token);
            },
            new RecordVmMetricsCommandHandler(new Mock<IVmMetricStore>(MockBehavior.Strict).Object), ConfigWithToken());

        await ep.HandleAsync(new RecordVmMetricsBody(DateTimeOffset.UtcNow,
        [
            new VmMetricsSampleBody("vmaaaaaaaaaaaaaaaaaaaaaaaaaa", "agent", 1, null, null, null, null, 0, 0),
        ]), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    private static string SolutionRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Titan.Domain")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new DirectoryNotFoundException("Could not locate the solution root.");
        }
        return dir.FullName;
    }

    // A callback that compares the token itself (==, !=, string.Equals) is not constant-time.
    [Fact]
    public void EveryEndpointThatReadsTheInternalToken_ComparesItThroughInternalTokenAuth()
    {
        string endpoints = Path.Combine(SolutionRoot(), "Titan.Infrastructure", "Endpoints");
        List<string> readers = Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("\"X-Internal-Token\""))
            .ToList();
        Assert.NotEmpty(readers);

        List<string> offenders = readers
            .Where(f => !File.ReadAllText(f).Contains("InternalTokenAuth.Matches("))
            .Select(f => Path.GetRelativePath(endpoints, f))
            .OrderBy(f => f)
            .ToList();

        Assert.Empty(offenders);
    }
}
