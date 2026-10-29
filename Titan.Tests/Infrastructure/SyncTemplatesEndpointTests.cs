using FastEndpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Options;
using Titan.Domain.Repositories.Catalog;
using Titan.Infrastructure.Endpoints.Catalog;

namespace Titan.Tests.Infrastructure;

// Only platform-admin may trigger template sync. The guard lives in
// Configure() (Roles("platform-admin")); this test pins it so dropping or widening
// the role requirement fails a test instead of silently opening the endpoint.
public class SyncTemplatesEndpointTests
{
    private static SyncTemplatesCommandHandler Handler()
    {
        var templates = new Mock<IVmTemplateRepository>();
        templates.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new SyncTemplatesCommandHandler(templates.Object, new ClusterOptions(),
            Mock.Of<IProviderCommandQueue>(), NullLogger<SyncTemplatesCommandHandler>.Instance);
    }

    [Fact]
    public void Configure_RequiresPlatformAdminRole_AndNoAnonymousAccess()
    {
        // Factory.Create already runs Configure() — do not call it again (roles would double up).
        var ep = Factory.Create<SyncTemplatesEndpoint>(Handler());

        Assert.NotNull(ep.Definition.AllowedRoles);
        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
    }

    // titan-ops architecture/http-contract.md#async-actions-return-202 — the sync is enqueued, not done, when the
    // response leaves: 202 and nothing attached.
    [Fact]
    public async Task HandleAsync_Answers202_WithAnEmptyBody()
    {
        var ep = Factory.Create<SyncTemplatesEndpoint>(Handler());
        ep.HttpContext.Response.Body = new MemoryStream();

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(202, ep.HttpContext.Response.StatusCode);
        Assert.Equal(0, ep.HttpContext.Response.Body.Length);
    }
}
