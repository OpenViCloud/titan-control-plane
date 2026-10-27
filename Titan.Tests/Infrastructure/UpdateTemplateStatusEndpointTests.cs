using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Options;
using Titan.Domain.Repositories.Catalog;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Tests.Infrastructure;

// The internal callback endpoint must 401 and NOT touch the template when the X-Internal-Token header is
// missing or wrong. Moq verification is used because the property under test is "the handler never reached
// the repository"; the endpoint holds the concrete handler, so the check sits on the repository it reads first.
public class UpdateTemplateStatusEndpointTests
{
    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static readonly UpdateTemplateStatusRequest ValidRequest = new(Report: "Created", ClusterId: "mock", ExternalId: "tpl-205");

    private static readonly ZoneOptions Zones = new() { Zones = [new ZoneConfig { Id = "mock", Name = "Mock" }] };

    [Fact]
    public async Task HandleAsync_MissingTokenHeader_Returns401_AndNeverTouchesTheTemplate()
    {
        var templates = new Mock<IVmTemplateRepository>();
        var handler = new UpdateTemplateStatusCommandHandler(templates.Object, Zones, NullLogger<UpdateTemplateStatusCommandHandler>.Instance);
        var ep = Factory.Create<UpdateTemplateStatusEndpoint>(
            _ => { /* no X-Internal-Token header */ },
            handler, ConfigWithToken("secret"));

        await ep.HandleAsync(ValidRequest, CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        templates.Verify(t => t.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WrongTokenHeader_Returns401_AndNeverTouchesTheTemplate()
    {
        var templates = new Mock<IVmTemplateRepository>();
        var handler = new UpdateTemplateStatusCommandHandler(templates.Object, Zones, NullLogger<UpdateTemplateStatusCommandHandler>.Instance);
        var ep = Factory.Create<UpdateTemplateStatusEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "wrong",
            handler, ConfigWithToken("secret"));

        await ep.HandleAsync(ValidRequest, CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        templates.Verify(t => t.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A worker older than per-zone templates reports no zone: whose alias it is cannot be told, so nothing is
    // recorded (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-zone).
    [Fact]
    public async Task HandleAsync_WithoutAZone_Returns400_AndNeverTouchesTheTemplate()
    {
        var templates = new Mock<IVmTemplateRepository>();
        var handler = new UpdateTemplateStatusCommandHandler(templates.Object, Zones, NullLogger<UpdateTemplateStatusCommandHandler>.Instance);
        var ep = Factory.Create<UpdateTemplateStatusEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "secret",
            handler, ConfigWithToken("secret"));

        await ep.HandleAsync(new UpdateTemplateStatusRequest(Report: "Created", ClusterId: null, ExternalId: "tpl-205"), CancellationToken.None);

        Assert.Equal(400, ep.HttpContext.Response.StatusCode);
        templates.Verify(t => t.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
