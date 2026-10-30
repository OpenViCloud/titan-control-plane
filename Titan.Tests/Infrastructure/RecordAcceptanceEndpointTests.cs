using System.Net;
using FastEndpoints;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.Legal;
using Titan.Application.Services.Platform.Legal;
using Titan.Domain.Aggregates.Acceptance;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Legal;

namespace Titan.Tests.Infrastructure;

// RecordAcceptanceEndpoint carries its own logic (pulling the source IP off the connection for the audit
// record), so it gets an endpoint-level test separate from LegalServiceTests. The endpoint takes a concrete
// handler, so it runs for real and the IP is read off the Acceptance it hands the repository.
public class RecordAcceptanceEndpointTests
{
    private readonly Mock<IAcceptanceRepository> _repo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private Acceptance? _recorded;

    public RecordAcceptanceEndpointTests()
    {
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("kc-user-1");
        _repo.Setup(r => r.AddAsync(It.IsAny<Acceptance>(), It.IsAny<CancellationToken>()))
             .Callback<Acceptance, CancellationToken>((a, _) => _recorded = a)
             .Returns(Task.CompletedTask);
    }

    private RecordAcceptanceCommandHandler Handler() =>
        new(new LegalDocumentCatalog(), _repo.Object, _currentUser.Object);

    [Fact]
    public async Task HandleAsync_ForwardsRemoteIpToService()
    {
        var ep = Factory.Create<RecordAcceptanceEndpoint>(
            ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7"),
            Handler());

        await ep.HandleAsync(CancellationToken.None);

        Assert.NotNull(_recorded);
        Assert.Equal("203.0.113.7", _recorded!.SourceIp);
    }

    [Fact]
    public async Task HandleAsync_NoRemoteIp_ForwardsNull()
    {
        var ep = Factory.Create<RecordAcceptanceEndpoint>(Handler()); // no connection IP set

        await ep.HandleAsync(CancellationToken.None);

        Assert.NotNull(_recorded);
        Assert.Null(_recorded!.SourceIp);
    }
}
