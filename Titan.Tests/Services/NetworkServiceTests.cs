using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Queries.Networking.Network;
using Titan.Application.Queries.Networking.Network.Contracts;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Endpoints.Network;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Networking;
using Titan.Domain.Repositories.Platform;
using Titan.Domain.Values;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class NetworkServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly Mock<INetworkRepository> _repo = new();
    private readonly Mock<IFirewallRuleRepository> _firewallRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Titan.Tests.Fakes.FakeActivityRecorder _activity = new();

    // Facade over the network command and query handlers so the tests keep one call shape; it proves
    // nothing on its own — the handlers underneath are the real ones.
    private NetworkFacade CreateService() =>
        new(_repo.Object, _firewallRepo.Object, _tenantRepo.Object, _zones, _currentUser.Object, _commands.Object, _activity);

    private readonly Titan.Application.Options.ZoneOptions _zones = new()
    {
        Zones = [new() { Id = "zone-1", Name = "Zone 1", Endpoint = "https://localhost:8443", ServerFingerprint = new string('a', 64) }],
    };

    private sealed class NetworkFacade(
        INetworkRepository repo, IFirewallRuleRepository firewallRepo, ITenantRepository tenantRepo,
        Titan.Application.Options.ZoneOptions zones,
        ICurrentUser currentUser, IProviderCommandQueue commands, IActivityRecorder activity)
    {
        public Task<CreateNetworkCommandResult> CreateAsync(CreateNetworkCommand req, CancellationToken ct = default)
            => new CreateNetworkCommandHandler(repo, tenantRepo, currentUser, commands, activity, zones).HandleAsync(req, ct);

        public Task<List<NetworkQueryResult>> ListAsync(CancellationToken ct = default)
            => new ListNetworksQueryHandler(repo, currentUser).HandleAsync(new ListNetworksQuery(), ct);

        public Task<NetworkQueryResult> GetAsync(Guid networkId, CancellationToken ct = default)
            => new GetNetworkQueryHandler(repo, currentUser).HandleAsync(new GetNetworkQuery(networkId), ct);

        public Task RenameAsync(Guid networkId, RenameNetworkRequest req, CancellationToken ct = default)
            => new RenameNetworkCommandHandler(repo, currentUser).HandleAsync(new RenameNetworkCommand(networkId, req.Name), ct);

        public Task DeleteAsync(Guid networkId, CancellationToken ct = default)
            => new DeleteNetworkCommandHandler(repo, currentUser, commands, activity)
                .HandleAsync(new DeleteNetworkCommand(networkId), ct);

        public Task UpdateStatusAsync(Guid networkId, UpdateNetworkStatusRequest req, CancellationToken ct = default)
            => new UpdateNetworkStatusCommandHandler(repo, firewallRepo, activity)
                .HandleAsync(new UpdateNetworkStatusCommand(networkId, req.Report, req.Exists, req.Status, req.ExternalId,
                    req.Cidr, req.Gateway, req.Reason, req.Detail), ct);
    }

    // The worker's four reports (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/network/design.md#network-reconciler).
    private static UpdateNetworkStatusRequest Observed(string status) => new("Observed", Exists: true, Status: status);
    private static readonly UpdateNetworkStatusRequest NotFoundReport = new("Observed", Exists: false);
    private static UpdateNetworkStatusRequest CreatedReport(string id, string? cidr, string? gateway) =>
        new("Created", ExternalId: id, Cidr: cidr, Gateway: gateway);
    private static readonly UpdateNetworkStatusRequest DeletedReport = new("Deleted");
    private static UpdateNetworkStatusRequest GaveUp(string reason, string? id = null, string? cidr = null, string? gateway = null) =>
        new("RequestFailed", ExternalId: id, Cidr: cidr, Gateway: gateway, Reason: reason, Detail: "raw incus error");

    // ── CreateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Success_ReturnsCreatingDto()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#create-returns-id: the response carries only the id, so the created network's own fields are
        // asserted on the instance that was added — the layer that still owns them.
        Network? added = null;
        _repo.Setup(r => r.Add(It.IsAny<Network>())).Callback<Network>(n => added = n);

        var result = await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default);

        Assert.NotNull(added);
        Assert.Equal(added!.Id, result.NetworkId);
        Assert.Null(added.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, added.CurrentExistence);
        Assert.Equal("my-net", added.Name);
        Assert.Null(added.Cidr);
        _repo.Verify(r => r.Add(It.IsAny<Network>()), Times.Once);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueNetworkCreateAsync(
            It.IsAny<Guid>(), "my-net", "zone-1", TenantProject, null, "Allow", "Allow", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_SendsBothPoliciesToTheWorker()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);

        await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", null, FirewallPolicy.Deny, FirewallPolicy.Allow), default);

        _commands.Verify(w => w.EnqueueNetworkCreateAsync(
            It.IsAny<Guid>(), "my-net", "zone-1", TenantProject, null, "Deny", "Allow", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // A zone Titan does not have must not fall through to a default cluster
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#zone-is-a-real-choice).
    [Fact]
    public async Task Create_UnknownZone_ThrowsNotFound_AndEnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService().CreateAsync(new CreateNetworkCommand("my-net", "nowhere"), default));

        Assert.Equal("ZONE_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Network>()), Times.Never);
        Assert.Empty(_commands.Invocations);
    }

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant(maxNetworks: 1);
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(1);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default));

        Assert.Equal("NETWORK_QUOTA_EXCEEDED", ex.Code);
    }

    [Fact]
    public async Task Create_NameTaken_ThrowsConflictException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default));

        Assert.Equal("NETWORK_NAME_TAKEN", ex.Code);
    }

    [Fact]
    public async Task Create_CidrOverlap_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);
        _repo.Setup(r => r.HasCidrOverlapAsync(TenantId, "10.0.0.0/24", default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", "10.0.0.0/24"), default));

        Assert.Equal("NETWORK_CIDR_OVERLAP", ex.Code);
    }

    [Fact]
    public async Task Create_NoCidr_SkipsCidrOverlapCheck()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);

        await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default);

        _repo.Verify(r => r.HasCidrOverlapAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_NoTenantContext_ThrowsForbiddenException()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default));
    }

    // ── ListAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsTenantNetworks()
    {
        SetupCurrentUser(TenantId);
        var networks = new List<Network> { Network.CreateUnchecked(TenantId, "net-1", "zone-1") };
        _repo.Setup(r => r.ListByTenantAsync(TenantId, default)).ReturnsAsync(networks);

        var result = await CreateService().ListAsync(default);

        Assert.Single(result);
        Assert.Equal("net-1", result[0].Name);
    }

    // The raw error of a give-up is admin-only (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure).
    [Fact]
    public void TenantNetworkQueryResult_HasNoFailureDetailField()
    {
        Assert.Null(typeof(NetworkQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(NetworkQueryResult).GetProperty("DesiredState"));
        Assert.NotNull(typeof(NetworkQueryResult).GetProperty("RequestFailureReason"));
        Assert.NotNull(typeof(NetworkQueryResult).GetProperty("CurrentExistence"));
    }

    // ── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_NetworkNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Network?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetAsync(Guid.NewGuid(), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Get_NetworkBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var network = Network.CreateUnchecked(Guid.NewGuid(), "other-net", "zone-1"); // different tenantId
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().GetAsync(network.Id, default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            network.Id, "cs-net-id", Tenant.ProjectNameFor(network.TenantId), "zone-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // RULES-ON-NETWORK-DELETE: the rules stay as Incus holds them until the network's delete takes the ACL.
    [Fact]
    public async Task Delete_LeavesTheRulesAlone()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        _firewallRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Delete_HasActiveVms_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(network.Id, default));

        Assert.Equal("NETWORK_NOT_EMPTY", ex.Code);
        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A load balancer lives on the network on Incus (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-and-vm-network).
    [Fact]
    public async Task Delete_WithALoadBalancerNotYetDeleted_IsNotEmpty()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);
        _repo.Setup(r => r.HasLiveLoadBalancersAsync(network.Id, default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(network.Id, default));

        Assert.Equal("NETWORK_NOT_EMPTY", ex.Code);
        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_NetworkBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var network = Network.CreateUnchecked(Guid.NewGuid(), "other-net", "zone-1"); // different tenantId
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(network.Id, default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Delete_ErrorNetworkWithExternalId_EnqueuesTheCommand()
    {
        // Incus reports it Errored — it still holds it, so the delete goes through Incus.
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        network.MarkObserved(NetworkStatus.Errored);
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            network.Id, "cs-net-id", Tenant.ProjectNameFor(network.TenantId), "zone-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ErrorNetworkWithoutExternalId_MarksDeletedLocally_NoCommand()
    {
        // The create gave up before Incus had the network — nothing on Incus, so delete locally and send nothing.
        SetupCurrentUser(TenantId);
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        network.MarkRequestFailed("reason", "detail");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RenameAsync ───────────────────────────────────────────────

    [Fact]
    public async Task Rename_Success_ChangesNameAndSaves()
    {
        // WHY: rename is CP-only metadata — it must persist the new name without touching
        // the provider (no delete/create command), so a tenant can relabel a live network.
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork(); // name "my-net"
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "renamed-net", default)).ReturnsAsync(false);

        await CreateService().RenameAsync(network.Id, new RenameNetworkRequest("renamed-net"), default);

        Assert.Equal("renamed-net", network.Name);
        Assert.Equal(NetworkStatus.Created, network.CurrentState); // status untouched
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.VerifyNoOtherCalls(); // no provider command for a rename
    }

    [Fact]
    public async Task Rename_NameTaken_ThrowsConflict_DoesNotSave()
    {
        // WHY: the name uniqueness invariant (per-tenant, non-deleted) must hold on rename,
        // not only on create — otherwise rename is a backdoor around NETWORK_NAME_TAKEN.
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "taken", default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().RenameAsync(network.Id, new RenameNetworkRequest("taken"), default));

        Assert.Equal("NETWORK_NAME_TAKEN", ex.Code);
        Assert.Equal("my-net", network.Name); // unchanged
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Rename_SameName_IsNoOp_SkipsNameCheckAndSave()
    {
        // WHY: renaming to the current name must not self-conflict against its own row,
        // and must not spend a DB write for a no-op.
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork(); // name "my-net"
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().RenameAsync(network.Id, new RenameNetworkRequest("my-net"), default);

        Assert.Equal("my-net", network.Name);
        _repo.Verify(r => r.ExistsByNameAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Rename_NetworkNotFound_ThrowsNotFound()
    {
        SetupCurrentUser(TenantId);
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Network?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RenameAsync(Guid.NewGuid(), new RenameNetworkRequest("x"), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Rename_NetworkBelongsToOtherTenant_ThrowsNotFound()
    {
        // WHY: cross-tenant isolation — a tenant must not rename (or even confirm existence of)
        // another tenant's network.
        SetupCurrentUser(TenantId);
        var network = Network.CreateUnchecked(Guid.NewGuid(), "other-net", "zone-1"); // different tenant
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RenameAsync(network.Id, new RenameNetworkRequest("x"), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Rename_DeletedNetwork_ThrowsInvalidState()
    {
        // WHY: a network being torn down or already gone has no meaningful name to change.
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        network.RequestDelete();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "x", default)).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RenameAsync(network.Id, new RenameNetworkRequest("x"), default));

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    // ── UpdateStatusAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatus_CreatedThenObservedCreated_KeepsTheIdAndAddress_AndRecordsTheIncusWord()
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, CreatedReport("cs-net-id", "10.0.0.0/24", "10.0.0.1"), default);
        await CreateService().UpdateStatusAsync(network.Id, Observed("Created"), default);

        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.Equal("10.0.0.1", network.Gateway);
        _repo.Verify(r => r.SaveAsync(default), Times.Exactly(2));
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#provider-state-is-verbatim
    [Theory]
    [InlineData("Pending", NetworkStatus.Pending)]
    [InlineData("Created", NetworkStatus.Created)]
    [InlineData("Errored", NetworkStatus.Errored)]
    [InlineData("Unknown", NetworkStatus.Unknown)]
    [InlineData("Unavailable", NetworkStatus.Unavailable)]
    public async Task UpdateStatus_EveryIncusStatus_IsRecordedAsIs(string word, NetworkStatus expected)
    {
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, Observed(word));

        Assert.Equal(expected, network.CurrentState);
        Assert.Equal("10.0.0.0/24", network.Cidr);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("created")]
    [InlineData("Error")]
    [InlineData("1")]
    [InlineData(null)]
    public async Task UpdateStatus_AWordIncusDoesNotReport_IsRefused_AndChangesNothing(string? word)
    {
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => CreateService().UpdateStatusAsync(network.Id, Observed(word!)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    [Fact]
    public async Task UpdateStatus_ObservedWithoutExists_IsRefused()
    {
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(network.Id, new UpdateNetworkStatusRequest("Observed", Status: "Created")));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task UpdateStatus_Deleted_MarksTheNetworkDeleted()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _firewallRepo.Setup(f => f.ListLiveOfNetworkForUpdateAsync(network.Id, default)).ReturnsAsync([]);

        await CreateService().UpdateStatusAsync(network.Id, DeletedReport, default);

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // RULES-ON-NETWORK-DELETE: the network's delete took its ACL, so every rule left in it is gone too; the rows stay.
    [Fact]
    public async Task UpdateStatus_Deleted_MarksEveryLiveRuleOfTheNetworkDeleted()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var enabled = Rule(network.Id);
        var deleting = Rule(network.Id);
        deleting.RequestDelete();
        _firewallRepo.Setup(f => f.ListLiveOfNetworkForUpdateAsync(network.Id, default)).ReturnsAsync([enabled, deleting]);

        await CreateService().UpdateStatusAsync(network.Id, DeletedReport, default);

        Assert.Equal(CurrentExistence.Deleted, enabled.CurrentExistence);
        Assert.Equal(CurrentExistence.Deleted, deleting.CurrentExistence);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // A rule whose own delete was waiting is done when its network's delete takes the ACL first: its request gets its
    // outcome here, once — its own delete command will only find it already Deleted. A rule nobody asked to delete
    // gets none.
    [Fact]
    public async Task UpdateStatus_Deleted_RecordsTheOutcomeOfARuleWhoseOwnDeleteWasWaiting_Only()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var enabled = Rule(network.Id);
        var deleting = Rule(network.Id);
        deleting.RequestDelete();
        _firewallRepo.Setup(f => f.ListLiveOfNetworkForUpdateAsync(network.Id, default)).ReturnsAsync([enabled, deleting]);

        await CreateService().UpdateStatusAsync(network.Id, DeletedReport, default);

        var ruleOutcome = Assert.Single(_activity.Events, e => e.Action == "firewall_rule.deleted");
        Assert.Equal(deleting.Id, ruleOutcome.ResourceId);
        Assert.Equal(TenantId, ruleOutcome.OutcomeTenantId);
    }

    // ── Reconciler observations ─────────────────────

    // The reconciler never reports the delete done (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#existence-axis): the
    // network not found keeps its delete waiting for the delete command.
    [Fact]
    public async Task UpdateStatus_NotFound_WhenADeleteWasAsked_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, NotFoundReport);

        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task UpdateStatus_NotFound_WhenTheNetworkIsStillWanted_ClearsTheStatus_AndKeepsWhatWasAsked()
    {
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, NotFoundReport);

        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Empty(_activity.Events);
    }

    // A network created WITHOUT a CIDR gets its Created report with empty cidr/gateway. It must still store the
    // provider id, or the reconciler never lists the network and its create waits forever.
    [Fact]
    public async Task UpdateStatus_CreateReportWithoutCidr_StillProvisions_AndRecordsCreated()
    {
        var network = Network.CreateUnchecked(TenantId, "no-cidr", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, CreatedReport("cs-net-9", "", ""));
        await CreateService().UpdateStatusAsync(network.Id, Observed("Created"));

        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal("cs-net-9", network.ExternalId);
        Assert.Null(network.Transition);
        Assert.Single(_activity.Events, e => e.Action == "network.created");
    }

    // A delete that failed leaves the customer free to press Delete again — a new request, a new command.
    [Fact]
    public async Task Delete_AfterTheFirstDeleteFailed_EnqueuesANewCommand()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);
        await CreateService().DeleteAsync(network.Id, default);
        await CreateService().UpdateStatusAsync(network.Id, GaveUp("Network deletion failed."));

        await CreateService().DeleteAsync(network.Id, default);

        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            network.Id, "cs-net-id", Tenant.ProjectNameFor(network.TenantId), "zone-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("Active")]
    [InlineData("Absent")]
    [InlineData("Error")]
    public async Task UpdateStatus_AReportTheWorkerNoLongerSends_ThrowsBadRequestException(string report)
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(network.Id, new UpdateNetworkStatusRequest(report, ExternalId: "cs-net-id"), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Null(network.ExternalId);
    }

    [Fact]
    public async Task UpdateStatus_NetworkNotFound_ThrowsNotFoundException()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Network?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), DeletedReport, default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // The tenant SetupTenant built; every provider call carries its Incus project
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#project-name).
    private Tenant? _tenant;
    private string TenantProject => _tenant!.ProjectName;

    // ── Activity recording ──────────────────────────────────────

    [Fact]
    public async Task Create_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);

        await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default);

        var e = Assert.Single(_activity.Events);
        Assert.Equal("network.create_requested", e.Action);
        Assert.Equal("network", e.ResourceType);
        Assert.Equal("my-net", e.ResourceName);
    }

    [Fact]
    public async Task Delete_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        Assert.Equal("network.delete_requested", Assert.Single(_activity.Events).Action);
    }

    // ── https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/activity/design.md#outcome-source: outcomes are recorded from the fulfilled request ──────────

    [Fact]
    public async Task UpdateStatus_FirstCreated_RecordsNetworkCreated_ForTheNetworksTenant_Once()
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        await CreateService().UpdateStatusAsync(network.Id, CreatedReport("cs-net", "10.0.0.0/24", "10.0.0.1"));

        await CreateService().UpdateStatusAsync(network.Id, Observed("Created"));
        await CreateService().UpdateStatusAsync(network.Id, Observed("Created"));

        var e = Assert.Single(_activity.Events);
        Assert.Equal("network.created", e.Action);
        Assert.Equal(TenantId, e.OutcomeTenantId);
        Assert.Equal("my-net", e.ResourceName);
    }

    [Fact]
    public async Task UpdateStatus_DeletingToDeleted_RecordsNetworkDeleted()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _firewallRepo.Setup(f => f.ListLiveOfNetworkForUpdateAsync(network.Id, default)).ReturnsAsync([]);

        await CreateService().UpdateStatusAsync(network.Id, DeletedReport);

        Assert.Equal("network.deleted", Assert.Single(_activity.Events).Action);
    }

    // Incus says Errored while the delete runs: an observation, not the command's answer. The delete keeps waiting,
    // so the give-up that follows still gives the network back instead of leaving it wanted-deleted.
    [Fact]
    public async Task UpdateStatus_ErroredWhileADeleteWaits_KeepsTheDeleteWaiting()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);
        await CreateService().DeleteAsync(network.Id, default);

        await CreateService().UpdateStatusAsync(network.Id, Observed("Errored"));

        Assert.Equal(NetworkStatus.Errored, network.CurrentState);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);

        await CreateService().UpdateStatusAsync(network.Id, GaveUp("Network deletion failed."));

        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Null(network.Transition);
        Assert.Equal("Network deletion failed.", network.RequestFailureReason);
    }

    [Fact]
    public async Task UpdateStatus_ErroredWhileACreateWaits_EndsTheCreate_AndRecordsNothing()
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        await CreateService().UpdateStatusAsync(network.Id, CreatedReport("13", "10.0.0.0/24", "10.0.0.1"));

        await CreateService().UpdateStatusAsync(network.Id, Observed("Errored"));

        Assert.Equal(NetworkStatus.Errored, network.CurrentState);
        Assert.Null(network.Transition);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task UpdateStatus_Created_StoresTheIdAndAddress_AndTheCreateKeepsWaiting()
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, CreatedReport("cs-net-id", "10.0.0.0/24", "10.0.0.1"));

        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
        Assert.Empty(_activity.Events);
    }

    // The network exists on Incus, so it must not be forgotten locally.
    [Fact]
    public async Task Delete_AfterCreatedThenRequestFailed_GoesToTheProvider()
    {
        SetupCurrentUser(TenantId);
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().UpdateStatusAsync(network.Id, CreatedReport("cs-net-id", "10.0.0.0/24", "10.0.0.1"));
        await CreateService().UpdateStatusAsync(network.Id, GaveUp("Network creation failed."));
        await CreateService().DeleteAsync(network.Id, default);

        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(network.Id, "cs-net-id", Tenant.ProjectNameFor(network.TenantId), "zone-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEqual(CurrentExistence.Deleted, network.CurrentExistence);
    }

    [Fact]
    public async Task UpdateStatus_RequestFailed_WhileADeleteWaits_GivesTheNetworkBack_WithTheReason()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);
        await CreateService().DeleteAsync(network.Id, default);
        _activity.Events.Clear();

        await CreateService().UpdateStatusAsync(network.Id, GaveUp("Network deletion failed."));

        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("Network deletion failed.", network.RequestFailureReason);
        Assert.Equal("raw incus error", network.RequestFailureDetail);
        Assert.Null(network.Transition);
        Assert.Empty(_activity.Events);
    }

    [Fact] // the create report was lost, the give-up report carries the id
    public async Task UpdateStatus_RequestFailedCarryingTheId_KeepsTheProviderId_AndTheReason()
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateService().UpdateStatusAsync(network.Id, GaveUp("Network creation failed.", "cs-net-id", "10.0.0.0/24", "10.0.0.1"));

        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.Equal("Network creation failed.", network.RequestFailureReason);
        Assert.Empty(_activity.Events);
    }

    [Fact] // never provisioned: request and outcome at once
    public async Task Delete_NeverProvisionedNetwork_RecordsRequestAndOutcomeTogether()
    {
        SetupCurrentUser(TenantId);
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        network.MarkRequestFailed("reason", "detail");
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        Assert.Equal(new[] { "network.delete_requested", "network.deleted" }, _activity.Events.Select(e => e.Action));
        Assert.Equal(TenantId, _activity.Events[1].OutcomeTenantId);
    }

    [Fact]
    public async Task Create_QuotaExceeded_RecordsNoActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant(maxNetworks: 1);
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(1);

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default));

        Assert.Empty(_activity.Events);
    }

    // A repeated delete is the same request, so it answers like the first
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat) and records and sends nothing.
    [Fact]
    public async Task Delete_Repeated_IsANoOp_RecordsNoActivity_EnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        var network = MakeActiveNetwork();
        network.RequestDelete(); // already Deleting
        _repo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _repo.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(false);

        await CreateService().DeleteAsync(network.Id, default);

        Assert.Empty(_activity.Events);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueNetworkDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Public IPv6: one /64 of the zone per network (https://github.com/OpenViCloud/titan-ops/blob/main/docs/infra/incus/design.md#ipv6-address-plan) ──

    private void SetupZoneCluster(string? uplink, string? block)
    {
        var cluster = _zones.Zones.Single(z => z.Id == "zone-1");
        cluster.OvnUplinkNetwork = uplink;
        cluster.StoragePool = "default";
        cluster.Ipv6Block = block;
    }

    private void SetupFreshCreate()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.CountPublicIpv6ActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "my-net", default)).ReturnsAsync(false);
        _repo.Setup(r => r.ListPublicIpv6IndexesInZoneAsync("zone-1", default)).ReturnsAsync(new HashSet<int>());
        _repo.Setup(r => r.ListPublicIpv6ReleasesInZoneAsync("zone-1", default))
            .ReturnsAsync(new Dictionary<int, DateTimeOffset?>());
    }

    // A /64 given back minutes ago would put the new network's router where OVH still sees the old one
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/network/design.md#ipv6-public): the handler hands the releases to the allocator.
    [Fact]
    public async Task Create_WithPublicIpv6_SkipsASlash64JustGivenBack_WhileOneWasNeverUsed()
    {
        SetupFreshCreate();
        SetupZoneCluster("ovnup", "2001:41d0:d00:d400::/56");
        _repo.Setup(r => r.ListPublicIpv6ReleasesInZoneAsync("zone-1", default))
            .ReturnsAsync(new Dictionary<int, DateTimeOffset?> { [1] = DateTimeOffset.UtcNow });
        Network? added = null;
        _repo.Setup(r => r.Add(It.IsAny<Network>())).Callback<Network>(n => added = n);

        await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", EnablePublicIpv6: true), default);

        Assert.NotNull(added);
        Assert.Equal(2, added!.PublicIpv6Index);
    }

    [Fact]
    public async Task Create_WithPublicIpv6_TakesTheLowestFreeSlash64_AndSendsItsRouter()
    {
        SetupFreshCreate();
        SetupZoneCluster("ovnup", "2001:41d0:d00:d400::/56");
        _repo.Setup(r => r.ListPublicIpv6IndexesInZoneAsync("zone-1", default)).ReturnsAsync(new HashSet<int> { 1, 2 });
        Network? added = null;
        _repo.Setup(r => r.Add(It.IsAny<Network>())).Callback<Network>(n => added = n);

        await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", EnablePublicIpv6: true), default);

        Assert.NotNull(added);
        Assert.Equal(3, added!.PublicIpv6Index);
        _commands.Verify(w => w.EnqueueNetworkCreateAsync(
            added.Id, "my-net", "zone-1", TenantProject, null, "Allow", "Allow",
            "2001:41d0:d00:d403::/64", "2001:41d0:d00:d400::103", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("ovnup", null)]
    [InlineData(null, "2001:41d0:d00:d400::/56")]
    public async Task Create_WithPublicIpv6_InAZoneWithoutUplinkOrBlock_IsRefused_AndNothingIsStaged(string? uplink, string? block)
    {
        SetupFreshCreate();
        SetupZoneCluster(uplink, block);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", EnablePublicIpv6: true), default));

        Assert.Equal("IPV6_NOT_AVAILABLE", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Network>()), Times.Never);
        _commands.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_WithPublicIpv6_InAZoneWithNoCluster_IsRefused()
    {
        SetupFreshCreate();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", EnablePublicIpv6: true), default));

        Assert.Equal("IPV6_NOT_AVAILABLE", ex.Code);
    }

    [Fact]
    public async Task Create_WithPublicIpv6_WhenEverySlash64IsTaken_IsRefused()
    {
        SetupFreshCreate();
        SetupZoneCluster("ovnup", "2001:41d0:d00:d400::/56");
        _repo.Setup(r => r.ListPublicIpv6IndexesInZoneAsync("zone-1", default)).ReturnsAsync(Enumerable.Range(1, 255).ToHashSet());

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", EnablePublicIpv6: true), default));

        Assert.Equal("IPV6_NOT_AVAILABLE", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Network>()), Times.Never);
    }

    // The cap counts the caller's networks with a /64; the factory refuses at it (QuotaInFactoryTests).
    [Fact]
    public async Task Create_WithPublicIpv6_AtTheTenantsIpv6Cap_IsRefused()
    {
        SetupFreshCreate();
        SetupZoneCluster("ovnup", "2001:41d0:d00:d400::/56");
        _repo.Setup(r => r.CountPublicIpv6ActiveByTenantAsync(TenantId, default)).ReturnsAsync(10);
        _repo.Setup(r => r.ListPublicIpv6IndexesInZoneAsync("zone-1", default)).ReturnsAsync(new HashSet<int>());

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1", EnablePublicIpv6: true), default));

        Assert.Equal("IPV6_QUOTA_EXCEEDED", ex.Code);
    }

    // Without IPv6 no /64 is looked for: a zone with no public IPv6 still makes networks.
    [Fact]
    public async Task Create_WithoutPublicIpv6_InAZoneWithNoIpv6_LooksForNo64()
    {
        SetupFreshCreate();

        await CreateService().CreateAsync(new CreateNetworkCommand("my-net", "zone-1"), default);

        _repo.Verify(r => r.ListPublicIpv6IndexesInZoneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    private void SetupTenant(int maxNetworks = 1)
    {
        var tenant = TestTenant.Active("acme", "owner-1", "admin@acme.com", maxNetworks: maxNetworks);
        _tenant = tenant;
        _tenantRepo.Setup(r => r.FindByIdAsync(TenantId, default)).ReturnsAsync(tenant);
    }

    private static FirewallRule Rule(Guid networkId)
    {
        var rule = FirewallRule.CreateUnchecked(networkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "10.0.0.0/24");
        rule.MarkActive($"acl/{Guid.NewGuid()}");
        return rule;
    }

    private static Network MakeActiveNetwork()
    {
        var network = Network.CreateUnchecked(TenantId, "my-net", "zone-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }
}
