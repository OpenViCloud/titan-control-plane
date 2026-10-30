using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.Network;
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

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task UpdateNetworkStatus_WithoutTheToken_Returns401(string? token)
    {
        var handler = new UpdateNetworkStatusCommandHandler(new Mock<INetworkRepository>(MockBehavior.Strict).Object, StrictActivity());
        var ep = Factory.Create<UpdateNetworkStatusEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["networkId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new UpdateNetworkStatusRequest("Created", ExternalId: "7", Cidr: "10.0.0.0/24", Gateway: "10.0.0.1"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task UpdateVmSnapshotStatus_WithoutTheToken_Returns401(string? token)
    {
        var handler = new UpdateVmSnapshotStatusCommandHandler(new Mock<IVmRepository>(MockBehavior.Strict).Object, StrictActivity());
        var ep = Factory.Create<UpdateSnapshotStatusEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["vmId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["snapshotId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new UpdateSnapshotStatusBody("Created", ExternalId: "vm1/s3"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task UpdateVolumeStatus_WithoutTheToken_Returns401(string? token)
    {
        var handler = new UpdateVolumeStatusCommandHandler(new Mock<IVolumeRepository>(MockBehavior.Strict).Object,
            new Mock<IVmRepository>(MockBehavior.Strict).Object,
            StrictActivity());
        var ep = Factory.Create<UpdateVolumeStatusEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["volumeId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new UpdateVolumeStatusBody("Created", ExternalId: "vol5"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task UpdateVolumeSnapshotStatus_WithoutTheToken_Returns401(string? token)
    {
        var handler = new UpdateVolumeSnapshotStatusCommandHandler(new Mock<IVolumeRepository>(MockBehavior.Strict).Object, StrictActivity());
        var ep = Factory.Create<UpdateVolumeSnapshotStatusEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["volumeId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["snapshotId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            handler, ConfigWithToken());

        await ep.HandleAsync(new UpdateVolumeSnapshotStatusBody("Created", ExternalId: "vol5/s9"), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task UpdateFirewallRuleStatus_WithoutTheToken_Returns401(string? token)
    {
        var repo = new Mock<INetworkRepository>(MockBehavior.Strict).Object;
        IActivityRecorder activity = StrictActivity();
        var ep = Factory.Create<UpdateFirewallRuleStatusEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["networkId"] = Guid.NewGuid().ToString();
                ctx.Request.RouteValues["ruleId"] = Guid.NewGuid().ToString();
                SetToken(ctx, token);
            },
            new UpdateFirewallRuleStatusCommandHandler(repo, activity),
            ConfigWithToken());

        await ep.HandleAsync(new UpdateFirewallRuleStatusBody("Created", ExternalRuleId: "acl/rule-1"), CancellationToken.None);

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
