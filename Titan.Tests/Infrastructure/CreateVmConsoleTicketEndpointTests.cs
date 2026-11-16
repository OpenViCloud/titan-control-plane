using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Vm;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// Taking a console ticket (titan-ops features/vm/design.md#vm-console): refusals a customer can act on come as JSON,
// before any websocket; a ticket is kept as its hash, for the caller, with no place taken.
public class CreateVmConsoleTicketEndpointTests
{
    private const string Ticket = "ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly ServiceProvider _services;
    private readonly ShellSessionLimiter _limiter;
    private readonly Vm _vm;

    public CreateVmConsoleTicketEndpointTests()
    {
        string database = $"console-tickets-endpoint-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<TitanDbContext>(o => o.UseInMemoryDatabase(database));
        _services = services.BuildServiceProvider();
        _limiter = new ShellSessionLimiter(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<ShellSessionLimiter>.Instance);
        _currentUser.SetupGet(u => u.TenantId).Returns(Owner.Id);
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("kc-1");
        _vm = Vm.Create(Owner.Id, Guid.NewGuid(), "web-1", "s", "tpl-1", "local-mac", 20);
        _vm.MarkCreated("vmabc");
        _vm.MarkObserved(VmStatus.Running, "10.0.0.5");
        _vms.Setup(r => r.FindByIdAsync(_vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_vm);
    }

    private TitanDbContext Db() => _services.CreateScope().ServiceProvider.GetRequiredService<TitanDbContext>();

    private CreateVmConsoleTicketEndpoint Endpoint(Guid vmId) =>
        Factory.Create<CreateVmConsoleTicketEndpoint>(ctx => ctx.Request.RouteValues["vmId"] = vmId.ToString(),
            new GetVmShellTargetQueryHandler(_vms.Object, _currentUser.Object),
            _currentUser.Object,
            _limiter,
            new ConsoleTicketStore(Db(), TimeProvider.System));

    [Fact]
    public async Task ATicketForARunningVm_Is204_AndKeptAsItsHash_ForTheCaller()
    {
        CreateVmConsoleTicketEndpoint endpoint = Endpoint(_vm.Id);

        await endpoint.HandleAsync(new CreateVmConsoleTicketRequest(Ticket), CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, endpoint.HttpContext.Response.StatusCode);
        ConsoleTicket row = Assert.Single(await Db().ConsoleTickets.ToListAsync());
        Assert.Equal(ConsoleTicketStore.HashOf(Ticket), row.TokenHash);
        Assert.Equal(_vm.Id, row.VmId);
        Assert.Equal(Owner.Id, row.TenantId);
        Assert.Equal("kc-1", row.CreatedBy);
    }

    [Fact]
    public async Task AnotherTenantsVm_IsNotFound_AndKeepsNoTicket()
    {
        Vm theirs = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "x", "s", "tpl-1", "local-mac", 20);
        theirs.MarkCreated("vmxyz");
        theirs.MarkObserved(VmStatus.Running, "10.0.0.6");
        _vms.Setup(r => r.FindByIdAsync(theirs.Id, It.IsAny<CancellationToken>())).ReturnsAsync(theirs);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            Endpoint(theirs.Id).HandleAsync(new CreateVmConsoleTicketRequest(Ticket), CancellationToken.None));

        Assert.Equal("VM_NOT_FOUND", refused.Code);
        Assert.Empty(await Db().ConsoleTickets.ToListAsync());
    }

    [Fact]
    public async Task AStoppedVm_IsInvalidStatus_AndKeepsNoTicket()
    {
        _vm.MarkObserved(VmStatus.Stopped, null);

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            Endpoint(_vm.Id).HandleAsync(new CreateVmConsoleTicketRequest(Ticket), CancellationToken.None));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
        Assert.Empty(await Db().ConsoleTickets.ToListAsync());
    }

    [Fact]
    public async Task ATenantWithFiveSessionsOpen_IsRefused_AndKeepsNoTicket()
    {
        for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
        {
            Assert.NotNull(await _limiter.TryAcquireAsync(Owner.Id, Guid.NewGuid(), CancellationToken.None));
        }

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            Endpoint(_vm.Id).HandleAsync(new CreateVmConsoleTicketRequest(Ticket), CancellationToken.None));

        Assert.Equal("VM_SESSION_LIMIT_REACHED", refused.Code);
        Assert.Empty(await Db().ConsoleTickets.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ovc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AMalformedTicket_FailsValidation(string ticket)
    {
        Assert.False(new CreateVmConsoleTicketValidator().Validate(new CreateVmConsoleTicketRequest(ticket)).IsValid);
    }

    [Fact]
    public void AWellFormedTicket_PassesValidation()
    {
        Assert.True(new CreateVmConsoleTicketValidator().Validate(new CreateVmConsoleTicketRequest(Ticket)).IsValid);
    }
}
