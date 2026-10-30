using FastEndpoints;
using Moq;
using Titan.Application.Commands.Billing.Wallet;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Tenant;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The admin's Active is where a tenant is activated by hand, and it orchestrates two modules: it opens the tenant's
// wallet (Billing) and activates it (Platform) — in the endpoint, since Platform depending on Billing would be a module
// cycle (titan-ops architecture/tenancy-and-admin.md#approval-is-activation). Pinned:
//   • the wallet is opened BEFORE the tenant is saved Active, so "an Active tenant has a wallet" is never false;
//   • no wallet for a tenant id that is not a tenant (nothing ties wallets to tenants in the database);
//   • Suspended, or a status the handler refuses, opens no wallet.
public class UpdateTenantStatusEndpointTests
{
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();

    private UpdateTenantStatusEndpoint Endpoint(Guid tenantId) =>
        Factory.Create<UpdateTenantStatusEndpoint>(
            ctx => ctx.Request.RouteValues["tenantId"] = tenantId.ToString(),
            new UpdateTenantStatusCommandHandler(_tenantRepo.Object),
            new OpenWalletCommandHandler(_walletRepo.Object, new CreditOptions { Currency = "VND" }),
            _tenantRepo.Object);

    private Tenant Known(Tenant tenant)
    {
        _tenantRepo.Setup(r => r.FindByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        return tenant;
    }

    private void VerifyNoWalletOpened() =>
        _walletRepo.Verify(w => w.Add(It.IsAny<Wallet>()), Times.Never);

    [Theory]
    [InlineData("Active")]
    [InlineData("active")] // the handler parses the status case-insensitively; the wallet must follow it
    public async Task Active_OnAPendingSignup_OpensTheWalletFirst_ThenActivates_Returns204(string status)
    {
        var tenant = Known(Tenant.CreatePending("acme", "kc-1", "a@acme.com"));
        var calls = new List<string>();
        _walletRepo.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("wallet")).Returns(Task.CompletedTask);
        _tenantRepo.Setup(r => r.SaveAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("tenant")).Returns(Task.CompletedTask);
        var ep = Endpoint(tenant.Id);

        await ep.HandleAsync(new UpdateStatusRequest(status), CancellationToken.None);

        Assert.Equal(204, ep.HttpContext.Response.StatusCode);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(new[] { "wallet", "tenant" }, calls);
        _walletRepo.Verify(w => w.Add(It.Is<Wallet>(x => x.TenantId == tenant.Id)), Times.Once);
    }

    // Enum.TryParse would read "1" (Active once Provisioning left the enum) and " Active" as Active; the endpoint and
    // the handler must agree on what Active is, or a tenant becomes Active without a wallet.
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
            // "Deleted" is refused by the handler; what matters here is the wallet
        }

        VerifyNoWalletOpened();
    }
}
