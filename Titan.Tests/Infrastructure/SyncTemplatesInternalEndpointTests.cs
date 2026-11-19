using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Options;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Tests.Infrastructure;

// The restore Job asks the admin's sync-all after it rebuilt a cluster, under the internal token
// (titan-ops features/backup/design.md#cluster-restore): a rebuilt host has none of the template images.
public class SyncTemplatesInternalEndpointTests
{
    private readonly Mock<IVmTemplateRepository> _templates = new();

    public SyncTemplatesInternalEndpointTests() =>
        _templates.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

    private SyncTemplatesCommandHandler Handler() =>
        new(_templates.Object, new ClusterOptions(), Mock.Of<IProviderCommandQueue>(),
            NullLogger<SyncTemplatesCommandHandler>.Instance);

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    [Fact]
    public async Task WrongToken_Returns401_AndSyncsNothing()
    {
        var ep = Factory.Create<SyncTemplatesInternalEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "wrong", Handler(), ConfigWithToken("secret"));

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        _templates.Verify(r => r.ListAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithTheToken_RunsTheSyncAll_AndAnswers202()
    {
        var ep = Factory.Create<SyncTemplatesInternalEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "secret", Handler(), ConfigWithToken("secret"));

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(202, ep.HttpContext.Response.StatusCode);
        _templates.Verify(r => r.ListAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
