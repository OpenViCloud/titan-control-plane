using FastEndpoints;
using Moq;
using Titan.Application.Commands.Billing.TenantActivation;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Infrastructure.Endpoints.Tenant;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The admin's Active is where a tenant is activated by hand. The endpoint reads the status once and sends Active to
// Billing's ActivateTenantCommandHandler, which opens the wallet in the same save
// (titan-ops architecture/tenancy-and-admin.md#approval-is-activation), and everything else to Platform's handler. Pinned:
//   • the wallet and the Active tenant commit together, in one save;
//   • no wallet for a tenant id that is not a tenant (nothing ties wallets to tenants in the database);
//   • Suspended, or a status the handler refuses, opens no wallet;
//   • a status that is not a status name is 400 before the tenant is looked up, so before its 404.
public class UpdateTenantStatusEndpointTests
{
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();

    public UpdateTenantStatusEndpointTests()
    {
        // A mock has no rows to lock, so the locked read answers what the plain one does.
        _tenantRepo.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => _tenantRepo.Object.FindByIdAsync(id, ct));
    }

    private UpdateTenantStatusEndpoint Endpoint(Guid tenantId) =>
        Factory.Create<UpdateTenantStatusEndpoint>(
            ctx => ctx.Request.RouteValues["tenantId"] = tenantId.ToString(),
            new UpdateTenantStatusCommandHandler(_tenantRepo.Object),
            new ActivateTenantCommandHandler(_tenantRepo.Object, _walletRepo.Object,
                new TenantActivation(_walletRepo.Object), new CreditOptions { Currency = "VND" }));

    private Tenant Known(Tenant tenant)
    {
        _tenantRepo.Setup(r => r.FindByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        return tenant;
    }

    private void VerifyNoWalletOpened() =>
        _walletRepo.Verify(w => w.Add(It.IsAny<Wallet>()), Times.Never);

    [Theory]
    [InlineData("Active")]
    [InlineData("active")] // the status is parsed case-insensitively; the wallet must follow it
    public async Task Active_OnAPendingSignup_OpensItsWallet_AndActivates_InOneSave_Returns204(string status)
    {
        var tenant = Known(Tenant.CreatePending("acme", "kc-1", "a@acme.com"));
        var ep = Endpoint(tenant.Id);

        await ep.HandleAsync(new UpdateStatusRequest(status), CancellationToken.None);

        Assert.Equal(204, ep.HttpContext.Response.StatusCode);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        _walletRepo.Verify(w => w.Add(It.Is<Wallet>(x => x.TenantId == tenant.Id)), Times.Once);
        _walletRepo.Verify(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _tenantRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Enum.TryParse would read "1" (Active once Provisioning left the enum) and " Active" as Active; the endpoint reads
    // the status once, so no reading of it activates without the wallet.
    [Theory]
    [InlineData("1")]
    [InlineData(" Active")]
    [InlineData("Active ")]
    public async Task ANonCanonicalActive_IsRefused_AndActivatesNothing_OpensNoWallet(string status)
    {
        var tenant = Known(Tenant.CreatePending("acme", "kc-1", "a@acme.com"));

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => Endpoint(tenant.Id).HandleAsync(new UpdateStatusRequest(status), CancellationToken.None));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(TenantStatus.PendingApproval, tenant.Status);
        VerifyNoWalletOpened();
    }

    [Fact]
    public async Task ANonStatus_OnAnUnknownTenant_Is400_Not404()
    {
        _tenantRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => Endpoint(Guid.NewGuid()).HandleAsync(new UpdateStatusRequest("1"), CancellationToken.None));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _tenantRepo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A status name that is no admin target is checked after the tenant is found: 404 for an unknown tenant.
    [Fact]
    public async Task ATargetNoAdminSets_OnAnUnknownTenant_Is404()
    {
        _tenantRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Endpoint(Guid.NewGuid()).HandleAsync(new UpdateStatusRequest("PendingApproval"), CancellationToken.None));
    }

    [Fact] // a suspended tenant already has its wallet: coming back keeps it, no second one
    public async Task Active_OnASuspendedTenantWithAWallet_KeepsThatWallet()
    {
        var tenant = TestTenant.Active("acme", "kc-1", "a@acme.com");
        tenant.Suspend();
        Known(tenant);
        _walletRepo.Setup(w => w.GetByTenantForUpdateAsync(tenant.Id, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Wallet.Open(tenant.Id, "VND"));

        await Endpoint(tenant.Id).HandleAsync(new UpdateStatusRequest("Active"), CancellationToken.None);

        Assert.Equal(TenantStatus.Active, tenant.Status);
        VerifyNoWalletOpened();
    }

    [Fact]
    public async Task Active_OnAnUnknownTenant_Is404_AndOpensNoWallet()
    {
        _tenantRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Tenant?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Endpoint(Guid.NewGuid()).HandleAsync(new UpdateStatusRequest("Active"), CancellationToken.None));

        VerifyNoWalletOpened();
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Deleted")]
    public async Task AnythingButActive_OpensNoWallet(string status)
    {
        var tenant = Known(TestTenant.Active("acme", "kc-1", "a@acme.com"));

        try
        {
            await Endpoint(tenant.Id).HandleAsync(new UpdateStatusRequest(status), CancellationToken.None);
        }
        catch (BadRequestException)
        {
            // "Deleted" is no status name, so the endpoint refuses it; what matters here is the wallet
        }

        VerifyNoWalletOpened();
    }
}
