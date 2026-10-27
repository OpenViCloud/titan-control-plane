using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Aggregates.Compute.SshKey;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Networking;
using Titan.Domain.Repositories.Platform;
using Titan.Application.Queries.Admin.Vm.Contracts;
using Titan.Tests.Domain;
using Titan.Tests.Fakes;


namespace Titan.Tests.Services;

public class VmServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly Mock<IVmRepository> _repo = new();
    private readonly Mock<INetworkRepository> _networkRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<IVmSnapshotRepository> _snapshotRepo = new();
    private readonly Mock<IVolumeRepository> _volumeRepo = CreateVolumeRepo();

    private static Mock<IVolumeRepository> CreateVolumeRepo()
    {
        var repo = new Mock<IVolumeRepository>();
        repo.Setup(r => r.ListAttachedToVmAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Volume>());
        return repo;
    }

    private readonly Mock<IVmOfferingRepository> _offeringRepo = new();
    private readonly Mock<IVmTemplateRepository> _templateRepo = new();
    private readonly Titan.Tests.Fakes.FakeActivityRecorder _activity = new();
    private readonly FakeSshKeyRepository _sshKeys = new();

    // Two configured clusters: "cluster-1" (where the fixture's networks live) and "cluster-2" (real, but not theirs).
    private readonly Titan.Application.Options.ClusterOptions _clusters = new()
    {
        Clusters = [new() { Id = "cluster-1", Name = "Cluster 1" }, new() { Id = "cluster-2", Name = "Cluster 2" }],
    };

    // The fixture's default offering ("offering-1", a medium tier) and template ("tmpl-1")
    // resolve out of the box so tests about quota/naming/userData stay about their own subject.
    // Any other id resolves to null (Moq default), which the unknown-* tests rely on.
    public VmServiceTests()
    {
        SetupOffering();
        SetupDefaultTemplate();
    }

    // One factory per handler (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#handlers): each line names exactly what
    // its operation touches, and no query handler can be handed _commands or _activity because its
    // constructor has nowhere to put them.
    private CreateVmCommandHandler CreateCreateHandler() =>
        new(_repo.Object, _networkRepo.Object, _tenantRepo.Object, _currentUser.Object,
            _commands.Object, _offeringRepo.Object, _templateRepo.Object, _sshKeys, _activity, _clusters);

    private RenameVmCommandHandler CreateRenameHandler() =>
        new(_repo.Object, _currentUser.Object);

    // Moq answers false to an unset IsBackendAsync: no load balancer sends to the VM unless a test says so.
    private readonly Mock<ILoadBalancerRepository> _loadBalancerRepo = new();

    private DeleteVmCommandHandler CreateDeleteHandler() =>
        new(_repo.Object, _tenantRepo.Object, _currentUser.Object, _commands.Object,
            _snapshotRepo.Object, _volumeRepo.Object, _loadBalancerRepo.Object, _activity);

    private StopVmCommandHandler CreateStopHandler() =>
        new(_repo.Object, _currentUser.Object, _commands.Object, _activity);

    private StartVmCommandHandler CreateStartHandler() =>
        new(_repo.Object, _currentUser.Object, _commands.Object, _activity);

    private UpdateVmStatusCommandHandler CreateUpdateStatusHandler() =>
        new(_repo.Object, _snapshotRepo.Object, _volumeRepo.Object, _activity);

    private MarkVmIntentFailedCommandHandler CreateMarkIntentFailedHandler() =>
        new(_repo.Object, _activity);

    private ListVmsQueryHandler CreateListHandler() =>
        new(_repo.Object, _currentUser.Object);

    private GetVmQueryHandler CreateGetHandler() =>
        new(_repo.Object, _currentUser.Object);

    private void SetupOffering(string id = "offering-1", decimal cpu = 1.0m, int vcpu = 2, int memoryMb = 4096) =>
        _offeringRepo.Setup(r => r.FindByIdAsync(id, default))
            .ReturnsAsync(VmOffering.Create(id, id, cpu, vcpu, memoryMb, pricePerHour: 311.256m));

    // Templates resolve out of a lookup so an id the fixture never registered returns null —
    // that is what the unknown-template test asserts on.
    private readonly Dictionary<Guid, VmTemplate> _knownTemplates = new();

    private void SetupDefaultTemplate()
    {
        _templateRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Guid id, CancellationToken _) => _knownTemplates.GetValueOrDefault(id));
        _templateId = SyncedTemplate(DefaultExternalTemplateId).Id.ToString();
    }

    // The external id the worker actually receives — the provider's own template id, not Titan's GUID.
    private const string DefaultExternalTemplateId = "77";
    private string _templateId = "";

    // The template's own tier is deliberately NOT the tier the create tests ask for
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-borrows-sizing): if both sources agreed, an assertion on
    // the provisioned size could not tell them apart.
    private const string TemplateOwnTier = "l";

    // Synced in cluster-1, the cluster the create tests use (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-cluster).
    private VmTemplate SyncedTemplate(string externalId, string clusterId = "cluster-1")
    {
        var t = VmTemplate.Create("Ubuntu-Web", null, "Ubuntu-22.04", 20, TemplateOwnTier);
        t.RequestSyncIn(clusterId);
        t.MarkCreatedIn(clusterId, externalId);
        _knownTemplates[t.Id] = t;
        return t;
    }

    private VmTemplate PendingTemplate()
    {
        var t = VmTemplate.Create("Pending-Tpl", null, "img", 10, TemplateOwnTier);
        _knownTemplates[t.Id] = t;
        return t;
    }

    // ── CreateAsync ───────────────────────────────────────────────────────

    // The VM row and its provider command commit in one SaveAsync
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table): enqueued after the save, the command
    // would be staged in a context nobody saves again, and the VM would wait for a worker that never hears of it.
    [Fact]
    public async Task Create_EnqueuesTheProviderCommand_BeforeItSaves()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        List<string> calls = new();
        _repo.Setup(r => r.SaveAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("save"))
            .Returns(Task.CompletedTask);
        _commands.Setup(w => w.EnqueueVmCreateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("enqueue"))
            .Returns(Task.CompletedTask);

        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null), default);

        Assert.Equal(["enqueue", "save"], calls);
    }

    [Fact]
    public async Task Create_Success_AddsAVmWaitingForItsCreate()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#create-returns-id: the response carries only the id, so the starting status is asserted on the
        // VM that was actually added.
        Vm? added = null;
        _repo.Setup(r => r.Add(It.IsAny<Vm>())).Callback<Vm>(v => added = v);

        var result = await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null), default);

        Assert.NotNull(added);
        Assert.Equal(added!.Id, result.VmId);
        Assert.Null(added.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, added.CurrentExistence);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Once);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), "web-01", "offering-1", 1.0m, 2, 4096, "77", "cluster-1",
            "cs-net-id", TenantProject, 0, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-borrows-sizing — the customer's chosen tier is what
    // provisions the VM. The template points at a DIFFERENT tier ("l"), so the two sources disagree by
    // construction and the assertion below can only be satisfied by the offering, across three tiers.
    [Theory]
    [InlineData("s", 0.5, 1, 2048)]
    [InlineData("m", 1.0, 2, 4096)]
    [InlineData("xl", 4.0, 8, 16384)]
    public async Task Create_ProvisionsTheTierTheCustomerChose_NotTheOneTheTemplatePointsAt(
        string chosenTier, double cpu, int vcpu, int memoryMb)
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering(chosenTier, (decimal)cpu, vcpu, memoryMb);
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, chosenTier, _templateId, "cluster-1", null), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), "web-01", chosenTier, (decimal)cpu, vcpu, memoryMb, "77", "cluster-1",
            "cs-net-id", TenantProject, 0, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The debt ceiling gates NOTHING (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#postpaid-no-brake): a tenant
    // whose ceiling is 0 still creates a VM. Only a passing create is evidence that no gate exists.
    [Fact]
    public async Task Create_TenantAtADebtCeilingOfZero_StillSucceeds_NoMoneyGate()
    {
        SetupCurrentUser(TenantId);
        SetupTenant(debtCeiling: 0m);
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#create-returns-id: the response carries only the id, so the starting status is asserted on the
        // VM that was actually added.
        Vm? added = null;
        _repo.Setup(r => r.Add(It.IsAny<Vm>())).Callback<Vm>(v => added = v);

        var result = await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null), default);

        Assert.NotNull(added);
        Assert.Equal(added!.Id, result.VmId);
        Assert.Null(added.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, added.CurrentExistence);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Once);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#vm-offering-sizing — the offering's sizing must reach the worker,
    // which sets it as the instance's limits.cpu / limits.memory; otherwise the VM silently gets a default
    // size whatever tier the customer picked.
    [Fact]
    public async Task Create_PassesSelectedOfferingSizingToTheCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering("xl", cpu: 4.0m, vcpu: 8, memoryMb: 16384);
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "xl", _templateId, "cluster-1", null), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), "web-01", "xl", 4.0m, 8, 16384, "77", "cluster-1",
            "cs-net-id", TenantProject, 0, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Templates are a curated Titan catalog
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-desired-state), so the customer-facing id is a GUID and the
    // control plane must translate it to the synced ExternalTemplateId — sending the GUID straight
    // through fails provisioning at the provider.
    [Fact]
    public async Task Create_PassesExternalTemplateIdToTheCommand_NotTheTitanGuid()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var template = SyncedTemplate(externalId: "99");

        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", template.Id.ToString(), "cluster-1", null), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), "web-01", "offering-1", 1.0m, 2, 4096, "99", "cluster-1",
            "cs-net-id", TenantProject, 0, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Each cluster has its own image alias: the VM gets the id of ITS cluster, never another cluster's.
    [Fact]
    public async Task Create_PassesTheTemplateIdOfTheVmsCluster()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = Network.CreateUnchecked(TenantId, "net-2", "cluster-2");
        network.MarkActive("cs-net-2", "10.0.1.0/24", "10.0.1.1");
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var template = SyncedTemplate(externalId: "99", clusterId: "cluster-1");
        template.RequestSyncIn("cluster-2");
        template.MarkCreatedIn("cluster-2", "3");

        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", template.Id.ToString(), "cluster-2", null), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), "web-01", "offering-1", 1.0m, 2, 4096, "3", "cluster-2",
            "cs-net-2", TenantProject, 0, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Synced in another cluster only: in this cluster the template does not exist yet.
    [Fact]
    public async Task Create_TemplateSyncedOnlyInAnotherCluster_ThrowsTemplateNotSynced_AndAddsNoVm()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var elsewhere = SyncedTemplate(externalId: "3", clusterId: "cluster-2");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", elsewhere.Id.ToString(), "cluster-1", null), default));

        Assert.Equal("TEMPLATE_NOT_SYNCED", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
        Assert.Empty(_commands.Invocations);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-listed: a template customers are not offered does not exist for them,
    // even when its image is on the cluster.
    [Fact]
    public async Task Create_UnlistedTemplate_ThrowsTemplateNotFound_AndEnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        VmTemplate hidden = TemplateSeedFields.Unlisted(SyncedTemplate("78"));

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateCreateHandler().HandleAsync(
            new CreateVmCommand("web-01", network.Id, "offering-1", hidden.Id.ToString(), "cluster-1", null), default));

        Assert.Equal("TEMPLATE_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
        Assert.Empty(_commands.Invocations);
    }

    [Fact]
    public async Task Create_UnknownTemplate_ThrowsNotFound_AndEnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", Guid.NewGuid().ToString(), "cluster-1", null), default));

        Assert.Equal("TEMPLATE_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
    }

    // The cluster the customer picks is where the VM lands (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#cluster-is-a-real-choice): a cluster
    // Titan does not have must not fall through to a default cluster.
    [Fact]
    public async Task Create_UnknownCluster_ThrowsNotFound_AndEnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateCreateHandler().HandleAsync(
            new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "nowhere", null), default));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
        Assert.Empty(_commands.Invocations);
    }

    // A VM is instantiated on its network's cluster; a network of another cluster is not there.
    [Fact]
    public async Task Create_NetworkOfAnotherCluster_ThrowsMismatch_AndAddsNoVm()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork(); // cluster-1
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateCreateHandler().HandleAsync(
            new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-2", null), default));

        Assert.Equal("NETWORK_CLUSTER_MISMATCH", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
        Assert.Empty(_commands.Invocations);
    }

    // A template row exists before the sync gives it an external id. Provisioning
    // against it would hand the worker a null id, so it must be refused with a distinct code
    // that tells the operator to run the sync rather than suggesting the template is missing.
    [Fact]
    public async Task Create_TemplateNotYetSynced_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var pending = PendingTemplate();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", pending.Id.ToString(), "cluster-1", null), default));

        Assert.Equal("TEMPLATE_NOT_SYNCED", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
    }

    // A template whose alias was seen gone keeps its provider id, so the NOT_SYNCED check alone does not
    // stop it. The customer catalog hides it; creating from it must be refused too.
    [Fact]
    public async Task Create_TemplateNotFoundInTheCluster_ThrowsTemplateUnavailable_AndAddsNoVm()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        SetupOffering();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        var broken = SyncedTemplate("7");
        broken.MarkNotFoundIn("cluster-1");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", broken.Id.ToString(), "cluster-1", null), default));

        Assert.Equal("TEMPLATE_UNAVAILABLE", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
    }

    // An unknown offering must fail with a domain-specific code, not the bare VALIDATION_ERROR,
    // which says nothing about what is missing.
    [Fact]
    public async Task Create_UnknownVmOffering_ThrowsNotFound_AndEnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        _offeringRepo.Setup(r => r.FindByIdAsync("ghost", default))
            .ReturnsAsync((Titan.Domain.Aggregates.Catalog.VmOffering.VmOffering?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "ghost", _templateId, "cluster-1", null), default));

        Assert.Equal("VM_OFFERING_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
    }

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant(maxVms: 1);
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(1);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateCreateHandler().HandleAsync(MakeCreateCommand(), default));

        Assert.Equal("VM_QUOTA_EXCEEDED", ex.Code);
    }

    [Fact]
    public async Task Create_NameTaken_ThrowsConflictException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateCreateHandler().HandleAsync(MakeCreateCommand(), default));

        Assert.Equal("VM_NAME_TAKEN", ex.Code);
    }

    [Fact]
    public async Task Create_NetworkNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        _networkRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Network?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateCreateHandler().HandleAsync(MakeCreateCommand(), default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Create_NetworkBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var otherNetwork = Network.CreateUnchecked(Guid.NewGuid(), "net-1", "cluster-1");
        _networkRepo.Setup(r => r.FindByIdAsync(otherNetwork.Id, default)).ReturnsAsync(otherNetwork);

        var req = new CreateVmCommand("web-01", otherNetwork.Id, "offering-1", _templateId, "cluster-1", null);
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateCreateHandler().HandleAsync(req, default));

        Assert.Equal("NETWORK_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Create_NetworkNotActive_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = Network.CreateUnchecked(TenantId, "net-1", "cluster-1"); // status = Creating
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var req = new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateCreateHandler().HandleAsync(req, default));

        Assert.Equal("NETWORK_NOT_ACTIVE", ex.Code);
    }

    [Fact]
    public async Task Create_NoTenantContext_ThrowsForbiddenException()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateCreateHandler().HandleAsync(MakeCreateCommand(), default));
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Success_EnqueuesTheCommand_StatusUnchanged()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        Assert.Equal(VmState.Running, vm.CurrentState); // reconciler handles transition, not CP
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
        _repo.Verify(r => r.SaveAsync(default), Times.Once); // the intent, request event and provider command together
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            vm.Id, "cs-vm-id", TenantProject, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-and-vm-network: refused rather than silently editing the load balancer.
    [Fact]
    public async Task Delete_WhileALoadBalancerSendsToTheVm_IsRefused_AndNothingIsSent()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        _loadBalancerRepo.Setup(r => r.IsBackendAsync(vm.Id, default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default));

        Assert.Equal("VM_IS_LOAD_BALANCER_BACKEND", ex.Code);
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // A snapshot operation in flight (Creating/Reverting) races the VM delete:
    // the snapshot command would act on an instance being deleted. CP
    // blocks the delete with 422 VM_SNAPSHOT_IN_PROGRESS until it settles.
    [Fact]
    public async Task Delete_SnapshotInProgress_ThrowsBusinessException_NoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.AnyInProgressByVmIdAsync(vm.Id, default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default));

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", ex.Code);
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The unprovisioned-VM shortcut (ExternalId == null → Destroy()) must
    // honor the same guard — otherwise the VM row vanishes under the snapshot op.
    [Fact]
    public async Task Delete_SnapshotInProgress_ErrorVmWithoutExternalId_AlsoBlocked()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.CreateFailed); // never provisioned → ExternalId null
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.AnyInProgressByVmIdAsync(vm.Id, default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default));

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", ex.Code);
        Assert.NotEqual(CurrentExistence.Deleted, vm.CurrentExistence);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Theory] // a create still waiting without a provider id may yet instantiate
    [InlineData(VmSeed.Creating)]
    public async Task Delete_InvalidStatus_ThrowsBusinessException(VmSeed status)
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(status);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_StoppedVm_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Stopped);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_OfACreateRefusedBeforeProvisioning_DeletesLocally_WithoutACommand()
    {
        // A VM that errored BEFORE it was ever provisioned has ExternalId == null
        // (it was never set, not cleared): no external resource exists, so mark destroyed
        // directly without a provider delete command.
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.CreateFailed); // never provisioned → ExternalId null
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A VM that provisioned (MarkRunning persisted an external id) and then failed on the provider
    // still EXISTS on the provider holding its NIC lease: the reconciler reports Error for a
    // failed VM, not just a gone one. Deleting it must terminate the provider resource, so the
    // external id must survive the Error transition, or delete takes the "never provisioned" shortcut
    // and orphans the lease.
    [Fact]
    public async Task Delete_ProvisionedThenErroredVm_EnqueuesTheCommandToTerminateProvider()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVmForTenant(TenantId);
        vm.MarkRunning("cs-vm-id", "10.0.0.2"); // provisioned at provider — external id persisted
        vm.MarkObserved(VmState.Error, null);  // Incus reports the instance in Error
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        // Must terminate the still-existing provider VM — not silently mark destroyed.
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            vm.Id, "cs-vm-id", TenantProject, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEqual(CurrentExistence.Deleted, vm.CurrentExistence); // reconciler owns the terminal transition
    }

    [Fact]
    public async Task Delete_VmBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVmForTenant(Guid.NewGuid());
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ── StopAsync / StartAsync ────────────────────────────────────

    // Stopping a Running VM enqueues the command and writes NO status: the reconciler is the single
    // writer, observes POWEROFF and reports Stopped.
    [Fact]
    public async Task Stop_WhenRunning_EnqueuesTheCommand_StatusUnchanged()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        Assert.Equal(VmState.Running, vm.CurrentState); // reconciler writes Stopped, not CP
        var intentId = Assert.IsType<Guid>(vm.PendingIntentId);
        _repo.Verify(r => r.SaveAsync(default), Times.Once); // the intent, request event and provider command together
        _commands.Verify(w => w.EnqueueVmStopAsync(
            vm.Id, intentId, "cs-vm-id", Tenant.ProjectNameFor(vm.TenantId), "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Stop follows Incus (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#requests-follow-incus): with no Incus status there is
    // nothing to stop — 422 VM_INVALID_STATUS, no command.
    [Theory]
    [InlineData(VmSeed.Creating)]
    [InlineData(VmSeed.CreateFailed)]
    [InlineData(VmSeed.NotFound)]
    [InlineData(VmSeed.Deleted)]
    public async Task Stop_InvalidStatus_ThrowsBusinessException(VmSeed status)
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(status);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        _commands.Verify(w => w.EnqueueVmStopAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Incus force-stops Ready, Frozen and Error instances too (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#requests-follow-incus).
    [Theory]
    [InlineData(VmSeed.Ready)]
    [InlineData(VmSeed.Frozen)]
    [InlineData(VmSeed.Error)]
    public async Task Stop_FromEveryStatusIncusStops_SendsTheStop(VmSeed status)
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(status);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        var intentId = Assert.IsType<Guid>(vm.PendingIntentId);
        _commands.Verify(w => w.EnqueueVmStopAsync(
            vm.Id, intentId, "cs-vm-id", Tenant.ProjectNameFor(vm.TenantId), "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // A VM owned by another tenant is invisible (404), never stopped.
    [Fact]
    public async Task Stop_VmBelongsToOtherTenant_ThrowsNotFound()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVmForTenant(Guid.NewGuid());
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
        _commands.Verify(w => w.EnqueueVmStopAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Starting a Stopped VM enqueues the command and writes NO status (reconciler observes
    // RUNNING and reports Running; it kept polling because Stopped is not a terminal state).
    [Fact]
    public async Task Start_WhenStopped_EnqueuesTheCommand_StatusUnchanged()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Stopped);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);

        Assert.Equal(VmState.Stopped, vm.CurrentState); // reconciler writes Running, not CP
        var intentId = Assert.IsType<Guid>(vm.PendingIntentId);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmStartAsync(
            vm.Id, intentId, "cs-vm-id", Tenant.ProjectNameFor(vm.TenantId), "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Start follows Incus: only Stopped and Frozen (a resume); Error is offered stop and delete only.
    [Theory]
    [InlineData(VmSeed.Creating)]
    [InlineData(VmSeed.CreateFailed)]
    [InlineData(VmSeed.NotFound)]
    [InlineData(VmSeed.Error)]
    [InlineData(VmSeed.Deleted)]
    public async Task Start_InvalidStatus_ThrowsBusinessException(VmSeed status)
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(status);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        _commands.Verify(w => w.EnqueueVmStartAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // Start on a Frozen instance is Incus's resume (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#requests-follow-incus)
    public async Task Start_OnAFrozenVm_SendsTheStart()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Frozen);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);

        _commands.Verify(w => w.EnqueueVmStartAsync(
            vm.Id, It.IsAny<Guid>(), "cs-vm-id", It.IsAny<string>(), "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // Ready is running: a start is already done, answered like the first request with nothing sent
    public async Task Start_OnAReadyVm_IsANoOp()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Ready);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);

        Assert.Null(vm.PendingIntent);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
        _commands.Verify(w => w.EnqueueVmStartAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A VM owned by another tenant is invisible (404), never started.
    [Fact]
    public async Task Start_VmBelongsToOtherTenant_ThrowsNotFound()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVmForTenant(Guid.NewGuid());
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ── RenameAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_Success_UpdatesName()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-renamed", default)).ReturnsAsync(false);

        await CreateRenameHandler().HandleAsync(new RenameVmCommand(vm.Id, "web-renamed"), default);

        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#contracts: the command returns nothing, so the rename is
        // observed on the aggregate; a caller reads it back through GetVmQuery.
        Assert.Equal("web-renamed", vm.Name);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task Rename_NameTaken_ThrowsConflict()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "taken", default)).ReturnsAsync(true);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateRenameHandler().HandleAsync(new RenameVmCommand(vm.Id, "taken"), default));

        Assert.Equal("VM_NAME_TAKEN", ex.Code);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Rename_SameName_NoConflict_Succeeds()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running); // name = "web-01"
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateRenameHandler().HandleAsync(new RenameVmCommand(vm.Id, "web-01"), default);

        // Renaming to the same name is a no-op that must not spend a uniqueness query. The name is
        // asserted on the aggregate because the command returns nothing (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#contracts).
        Assert.Equal("web-01", vm.Name);
        _repo.Verify(r => r.ExistsByNameAsync(It.IsAny<Guid>(), It.IsAny<string>(), default), Times.Never);
        // The handler recognises it as done before touching the aggregate
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat), so nothing is written either.
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rename_DeletedVm_ThrowsInvalidStatus()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Deleted);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateRenameHandler().HandleAsync(new RenameVmCommand(vm.Id, "web-x"), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task Rename_VmOtherTenant_ThrowsNotFound()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVmForTenant(Guid.NewGuid());
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateRenameHandler().HandleAsync(new RenameVmCommand(vm.Id, "web-x"), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ── UpdateStatusAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatus_ObservedRunning_RecordsTheStatusAndAddress()
    {
        var vm = MakeVm(VmSeed.CreatedNotSeen);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-id", IpAddress: "10.0.0.1"), default);

        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal("cs-vm-id", vm.ExternalId);
        Assert.Equal("10.0.0.1", vm.IpAddress);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_ObservedError_RecordsIt_AndKeepsTheProviderId()
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Error", ExternalId: null, IpAddress: null), default);

        Assert.Equal(VmState.Error, vm.CurrentState);
        Assert.Null(vm.IpAddress);
        // Incus Error is an instance whose qemu process still runs: the provider id stays, so a delete reaches it.
        Assert.Equal("cs-vm-id", vm.ExternalId);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // The worker sends a clean reason for the tenant and the raw diagnostic in Detail. Both persist; the detail is
    // what admins later read (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure).
    [Fact]
    public async Task UpdateStatus_RequestFailed_PersistsBothReasonAndDetail_AndLeavesNoIncusStatus()
    {
        var vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "RequestFailed",
            Reason: "VM provisioning failed due to a system error. Please try again or contact support.",
            Detail: "instance create failed: operation timed out"), default);

        Assert.Equal("VM provisioning failed due to a system error. Please try again or contact support.", vm.RequestFailureReason);
        Assert.Equal("instance create failed: operation timed out", vm.RequestFailureDetail);
        Assert.Null(vm.CurrentState);
        Assert.Null(vm.PendingIntent);
    }

    // The tenant-facing DTO must not carry the raw diagnostic at all. Asserting the value is
    // absent is not enough; the field itself must not exist, so no future mapping can leak it.
    [Fact]
    public void TenantVmQueryResult_HasNoFailureDetailField()
    {
        Assert.Null(typeof(VmQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(VmQueryResult).GetProperty("CurrentStateDetail"));
        Assert.Null(typeof(VmQueryResult).GetProperty("CurrentStateReason"));
        Assert.NotNull(typeof(VmQueryResult).GetProperty("RequestFailureReason"));
    }

    // The worker sends the Incus word for word: an in-between word Incus does not report for a QEMU instance, a
    // word of the old vocabulary, another casing, or a number is unknown (Enum.TryParse would take the last two).
    [Theory]
    [InlineData("Stopping")]
    [InlineData("Starting")]
    [InlineData("Destroyed")]
    [InlineData("running")]
    [InlineData("1")]
    public async Task UpdateStatus_AWordIncusDoesNotReport_IsAnUnknownStatus(string reported)
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, reported), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(VmState.Running, vm.CurrentState);
    }

    [Theory] // every status a QEMU instance reports (incus driver_qemu.go statusCode) is accepted, word for word
    [InlineData("Running", VmState.Running)]
    [InlineData("Ready", VmState.Ready)]
    [InlineData("Stopped", VmState.Stopped)]
    [InlineData("Frozen", VmState.Frozen)]
    [InlineData("Error", VmState.Error)]
    public async Task UpdateStatus_EveryIncusStatus_IsRecordedAsIs(string reported, VmState expected)
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, reported), default);

        Assert.Equal(expected, vm.CurrentState);
    }

    [Fact] // the reconciler did not find the instance
    public async Task UpdateStatus_ObservedNotExisting_IsNotFound()
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", false), default);

        Assert.Equal(CurrentExistence.NotFound, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
    }

    [Theory] // a malformed report is refused before anything is read
    [InlineData("Observed")]  // no Exists
    [InlineData("Destroyed")] // the old report name
    [InlineData("Absent")]
    public async Task UpdateStatus_MalformedReport_Is400(string report)
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, report), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    // The admin DTO does expose the raw diagnostic for debugging.
    [Fact]
    public void AdminVmQueryResult_CarriesTheFailureDetail()
    {
        var vm = MakeVmForTenant(TenantId);
        vm.MarkRequestFailed("clean customer message", "raw worker error");

        var dto = AdminVmQueryResult.From(vm);

        Assert.Equal("clean customer message", dto.RequestFailureReason);
        Assert.Equal("raw worker error", dto.RequestFailureDetail);
    }

    [Fact]
    public async Task UpdateStatus_Deleted_RecordsTheInstanceGone()
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Deleted"), default);

        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_Deleted_CascadesSnapshotDeletion()
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Deleted"), default);

        _snapshotRepo.Verify(r => r.MarkAllDeletedByVmIdAsync(vm.Id, default), Times.Once);
    }

    // A volume left attached to a deleted VM must not stay Attached for ever: it could not be detached, attached or
    // deleted, and its disk would still be billed (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#vm-delete-releases-volumes).
    [Fact]
    public async Task Delete_ThenDeleted_LeavesTheAttachedVolumeDeletable()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        Volume volume = AttachedVolume(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);
        Assert.Equal(VolumeTransition.Detaching, volume.Transition);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Deleted"), default);

        Assert.Null(volume.Transition);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.DesiredAttachedVmId);
        volume.RequestDelete(liveSnapshotCount: 0);
    }

    // Seen detached by the VM's Deleted whether or not a delete asked (a repeat that found it detaching, say). The
    // desired attachment is the customer's and stays: nothing waits, so it reads as drift.
    [Fact]
    public async Task UpdateStatus_Deleted_SeesItsVolumesDetached()
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        Volume volume = AttachedVolume(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Deleted"), default);

        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.Transition);
    }

    private Volume AttachedVolume(Vm vm)
    {
        Volume volume = Volume.CreateUnchecked(vm.TenantId, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        volume.Attach(vm);
        volume.MarkObserved(vm.Id, false);
        _volumeRepo.Setup(r => r.ListAttachedToVmAsync(vm.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Volume> { volume });
        return volume;
    }

    [Fact]
    // Incus says Running before the guest has an address: that is Running (the callback used to refuse it, and the
    // VM sat Creating until DHCP answered — or forever).
    public async Task UpdateStatus_RunningWithoutAnAddress_IsRunning_AndEndsTheCreate()
    {
        var vm = MakeVm(VmSeed.CreatedNotSeen);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running"), default);

        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Null(vm.IpAddress);
        Assert.Null(vm.PendingIntent);
        Assert.Equal("vm.created", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task UpdateStatus_InvalidStatus_ThrowsBadRequestException()
    {
        var vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Blah", null, null), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_VmNotFound_ThrowsNotFoundException()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Vm?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(Guid.NewGuid(), "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ── Cloud-init ──────────────────────────────────────────────

    [Fact]
    public async Task Create_WithUserData_PassesUserDataToTheCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var userData = "#!/bin/bash\necho hello";
        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null, userData), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            userData, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithoutUserData_PassesNullToTheCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── SSH keys picked at create (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-key) ──────

    private Network ReadyToCreate()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);
        return network;
    }

    [Fact]
    public async Task Create_WithSshKeys_SendsVendorDataCarryingEachKeyLine()
    {
        var network = ReadyToCreate();
        var ed = SshKey.CreateUnchecked(TenantId, "laptop", SshPublicKeyTests.Ed25519);
        var rsa = SshKey.CreateUnchecked(TenantId, "desktop", SshPublicKeyTests.Rsa2048);
        _sshKeys.Saved.AddRange([ed, rsa]);
        string? vendorData = null;
        _commands.Setup(w => w.EnqueueVmCreateAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(i => vendorData = (string?)i.Arguments[12]))
            .Returns(Task.CompletedTask);

        await CreateCreateHandler().HandleAsync(new CreateVmCommand(
            "web-01", network.Id, "offering-1", _templateId, "cluster-1", null, SshKeyIds: [ed.Id, rsa.Id, ed.Id]), default);

        Assert.NotNull(vendorData);
        Assert.Equal(1, CountOccurrences(vendorData, ed.PublicKey));
        Assert.Equal(1, CountOccurrences(vendorData, rsa.PublicKey));
        Assert.DoesNotContain("alice@laptop", vendorData);
    }

    private static int CountOccurrences(string text, string part) =>
        (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public async Task Create_WithoutSshKeys_SendsNoVendorData()
    {
        var network = ReadyToCreate();

        await CreateCreateHandler().HandleAsync(new CreateVmCommand(
            "web-01", network.Id, "offering-1", _templateId, "cluster-1", null), default);

        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string?>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithAnUnknownKeyId_IsSshKeyNotFound()
    {
        var network = ReadyToCreate();
        _sshKeys.Saved.Add(SshKey.CreateUnchecked(TenantId, "laptop", SshPublicKeyTests.Ed25519));

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => CreateCreateHandler().HandleAsync(
            new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null, SshKeyIds: [Guid.NewGuid()]),
            default));

        Assert.Equal("SSH_KEY_NOT_FOUND", refused.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithAnotherTenantsKey_IsSshKeyNotFound_AndMakesNothing()
    {
        var network = ReadyToCreate();
        var theirs = SshKey.CreateUnchecked(Guid.NewGuid(), "theirs", SshPublicKeyTests.Ed25519);
        _sshKeys.Saved.Add(theirs);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => CreateCreateHandler().HandleAsync(
            new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null, SshKeyIds: [theirs.Id]),
            default));

        Assert.Equal("SSH_KEY_NOT_FOUND", refused.Code);
        _repo.Verify(r => r.Add(It.IsAny<Vm>()), Times.Never);
        _commands.Verify(w => w.EnqueueVmCreateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithUserData_VmEntityHasUserData()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        Vm? savedVm = null;
        _repo.Setup(r => r.Add(It.IsAny<Vm>())).Callback<Vm>(vm => savedVm = vm);

        var userData = "#!/bin/bash\necho hello";
        await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null, userData), default);

        Assert.NotNull(savedVm);
        Assert.Equal(userData, savedVm!.UserData);
    }

    [Fact]
    public void VmQueryResult_UserDataReturned()
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0,
            "#!/bin/bash\necho hello");
        var dto = VmQueryResult.From(vm);
        Assert.Equal("#!/bin/bash\necho hello", dto.UserData);
    }

    [Fact]
    public void VmQueryResult_NoUserData_ReturnsNull()
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);
        var dto = VmQueryResult.From(vm);
        Assert.Null(dto.UserData);
    }

    // ── UpdateStatus stores PublicIpv6Address ────────────────

    [Fact]
    public async Task UpdateStatus_Running_WithPublicIpv6_StoresAddress()
    {
        var vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1", PublicIpv6Address: "2001:db8::1"), default);

        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal("2001:db8::1", vm.PublicIpv6Address);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_Running_WithoutPublicIpv6_IpV6AddressNull()
    {
        var vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default);

        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Null(vm.PublicIpv6Address);
    }

    // ── VmQueryResult includes PublicIpv6Address ────────────────────────

    [Fact]
    public void VmQueryResult_WithIpv6_IncludesPublicIpv6Address()
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);
        vm.MarkRunning("cs-vm-1", "10.0.0.1", "2001:db8::1");

        var dto = VmQueryResult.From(vm);

        Assert.Equal("2001:db8::1", dto.PublicIpv6Address);
    }

    [Fact]
    public void VmQueryResult_WithoutIpv6_PublicIpv6AddressNull()
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);

        var dto = VmQueryResult.From(vm);

        Assert.Null(dto.PublicIpv6Address);
    }

    // ── list returns publicIpv6Address per item (mixed IPv6/non-IPv6) ──

    [Fact]
    public async Task List_MixedIpv6_EachItemHasPublicIpv6Address()
    {
        SetupCurrentUser(TenantId);
        var ipv6Vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "ipv6-vm", "offering-1", "tmpl-1", "cluster-1", 0);
        ipv6Vm.MarkRunning("cs-vm-1", "10.0.0.1", "2001:db8::1");
        var plainVm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "plain-vm", "offering-1", "tmpl-1", "cluster-1", 0);
        _repo.Setup(r => r.ListByTenantAsync(TenantId, default)).ReturnsAsync(new List<Vm> { ipv6Vm, plainVm });

        var result = await CreateListHandler().HandleAsync(new ListVmsQuery(), default);

        var ipv6Dto = result.Single(v => v.Name == "ipv6-vm");
        var plainDto = result.Single(v => v.Name == "plain-vm");
        Assert.Equal("2001:db8::1", ipv6Dto.PublicIpv6Address);
        Assert.Null(plainDto.PublicIpv6Address);
    }

    // ── A tenant never sees another tenant's VMs ───────────────

    [Fact]
    public async Task List_ScopesToCallerTenantOnly_NeverAcrossTenants()
    {
        SetupCurrentUser(TenantId);
        var callerVm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "mine", "offering-1", "tmpl-1", "cluster-1", 0);
        _repo.Setup(r => r.ListByTenantAsync(TenantId, default)).ReturnsAsync(new List<Vm> { callerVm });

        var result = await CreateListHandler().HandleAsync(new ListVmsQuery(), default);

        Assert.Single(result);
        Assert.Equal("mine", result[0].Name);
        // Visibility is scoped by the caller's tenant id — the service never queries another tenant.
        _repo.Verify(r => r.ListByTenantAsync(TenantId, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.ListByTenantAsync(It.Is<Guid>(g => g != TenantId), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Activity recording ──────────────────────────────────────

    // A successful create records vm.create_requested with the VM's name snapshot.
    [Fact]
    public async Task Create_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);
        _repo.Setup(r => r.ExistsByNameAsync(TenantId, "web-01", default)).ReturnsAsync(false);
        var network = MakeActiveNetwork();
        _networkRepo.Setup(r => r.FindByIdAsync(network.Id, default)).ReturnsAsync(network);

        var result = await CreateCreateHandler().HandleAsync(new CreateVmCommand("web-01", network.Id, "offering-1", _templateId, "cluster-1", null), default);

        var e = Assert.Single(_activity.Events);
        Assert.Equal("vm.create_requested", e.Action);
        Assert.Equal("vm", e.ResourceType);
        Assert.Equal(result.VmId, e.ResourceId);
        Assert.Equal("web-01", e.ResourceName);
        Assert.False(e.Saved); // staged, flushed atomically with the VM insert
    }

    // A guard-rejected create (quota) records nothing: only real writes are logged.
    [Fact]
    public async Task Create_QuotaExceeded_RecordsNoActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant(maxVms: 1);
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(1);

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateCreateHandler().HandleAsync(MakeCreateCommand(), default));

        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Delete_ProvisionedVm_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        var e = Assert.Single(_activity.Events);
        Assert.Equal("vm.delete_requested", e.Action); // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity: the request, not the outcome
        Assert.Equal(vm.Id, e.ResourceId);
        Assert.False(e.Saved); // staged, flushed with the pending intent and the provider command
    }

    [Fact]
    public async Task Delete_UnprovisionedErrorVm_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.CreateFailed); // never provisioned → ExternalId null
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        // The request and its outcome happen at once, so both rows go in one SaveChanges.
        Assert.Collection(_activity.Events,
            e => { Assert.Equal("vm.delete_requested", e.Action); Assert.Null(e.OutcomeTenantId); },
            e => { Assert.Equal("vm.deleted", e.Action); Assert.Equal(TenantId, e.OutcomeTenantId); });
        Assert.All(_activity.Events, e => Assert.False(e.Saved));
    }

    [Fact]
    public async Task Stop_WhenRunning_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        Assert.Equal("vm.stop_requested", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task Start_WhenStopped_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Stopped);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);

        Assert.Equal("vm.start_requested", Assert.Single(_activity.Events).Action);
    }

    // ── https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity: a request is applied once; its outcome is recorded once ─────

    // A second DELETE before the reconciler notices records no second request; it only enqueues the
    // same command again.
    [Fact]
    public async Task Delete_Repeated_RecordsOneRequest_AndResendsTheSameCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);
        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        Assert.Equal("vm.delete_requested", Assert.Single(_activity.Events).Action);
        // The repeat saves too: that save is what persists a command row the repeat reopened.
        _repo.Verify(r => r.SaveAsync(default), Times.Exactly(2));
        // Re-sent, not re-decided: same command key, which the queue absorbs unless the first run failed. A resend,
        // not a new request: a new request on an open row is another request's race and is refused.
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            vm.Id, "cs-vm-id", TenantProject, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        _commands.Verify(w => w.ResendVmDeleteAsync(
            vm.Id, "cs-vm-id", TenantProject, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_Repeated_RecordsOneRequest_AndResendsTheSameIntentId()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);
        var intentId = Assert.IsType<Guid>(vm.PendingIntentId);
        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        Assert.Equal("vm.stop_requested", Assert.Single(_activity.Events).Action);
        _commands.Verify(w => w.EnqueueVmStopAsync(
            vm.Id, intentId, "cs-vm-id", Tenant.ProjectNameFor(vm.TenantId), "cluster-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
        _commands.Verify(w => w.EnqueueVmStopAsync(
            vm.Id, It.Is<Guid>(g => g != intentId), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_Repeated_RecordsOneRequest()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Stopped);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);
        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);

        Assert.Equal("vm.start_requested", Assert.Single(_activity.Events).Action);
        // The repeat saves too: that save is what persists a command row the repeat reopened.
        _repo.Verify(r => r.SaveAsync(default), Times.Exactly(2));
    }

    // Asking for the state the VM is already in is done, not illegal
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity).
    [Fact]
    public async Task Stop_OnAStoppedVm_IsANoOp()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Stopped);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
        _commands.Verify(w => w.EnqueueVmStopAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_OnARunningVm_IsANoOp()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);

        Assert.Empty(_activity.Events);
        _commands.Verify(w => w.EnqueueVmStartAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_OnADeletedVm_IsANoOp()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Deleted);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(default), Times.Never);
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_WhileAStopIsWaiting_Throws()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    // The reconciler's observation is where the outcome is recorded, once, for the VM's tenant. A flip
    // nobody asked for, like a guest shutdown, records nothing (see the last test in this block).
    [Fact]
    public async Task UpdateStatus_FirstRunning_RecordsVmCreated()
    {
        var vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default);

        var e = Assert.Single(_activity.Events);
        Assert.Equal("vm.created", e.Action);
        Assert.Equal(TenantId, e.OutcomeTenantId);
        Assert.Equal(vm.Id, e.ResourceId);
        Assert.False(e.Saved); // staged, flushed with the status in the same SaveChanges
    }

    [Fact]
    public async Task UpdateStatus_FulfillingStopStartDelete_RecordsEachOutcome()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Stopped", ExternalId: null, IpAddress: null), default);
        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-id", IpAddress: "10.0.0.2"), default);
        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Deleted"), default);

        Assert.Equal(
            new[] { "vm.stop_requested", "vm.stopped", "vm.start_requested", "vm.started", "vm.delete_requested", "vm.deleted" },
            _activity.Events.Select(e => e.Action));
        Assert.All(_activity.Events.Where(e => !e.Action.EndsWith("_requested")),
            e => Assert.Equal(TenantId, e.OutcomeTenantId));
    }

    [Fact] // the reconciler reports Running every cycle
    public async Task UpdateStatus_RepeatedRunning_RecordsTheOutcomeOnce()
    {
        var vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default);

        Assert.Equal("vm.created", Assert.Single(_activity.Events).Action);
    }

    // ── The create command reports the provider id before the VM runs ──

    [Fact]
    public async Task UpdateStatus_Created_StoresTheProviderId_AndTheCreateKeepsWaiting()
    {
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Created", ExternalId: "cs-vm-abc"), default);

        Assert.Equal("cs-vm-abc", vm.ExternalId);
        Assert.Null(vm.CurrentState);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal(VmTransition.Creating, vm.Transition);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task UpdateStatus_RunningAfterCreated_RecordsVmCreatedOnce_NotVmStarted()
    {
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Created", ExternalId: "cs-vm-abc"), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Running", ExternalId: "cs-vm-abc", IpAddress: "10.0.0.1"), default);

        Assert.Equal("vm.created", Assert.Single(_activity.Events).Action);
        Assert.Null(vm.Transition);
    }

    [Fact]
    public async Task UpdateStatus_Created_WithoutAProviderId_Is400()
    {
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Created", ExternalId: ""), default));
    }

    // A1 of the resource sync review: the VM exists on the provider, so its delete must reach the provider.
    [Fact]
    public async Task UpdateStatus_RequestFailedAfterCreated_KeepsTheProviderId_AndDeleteGoesToTheProvider()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Created", ExternalId: "cs-vm-abc"), default);
        await CreateUpdateStatusHandler().HandleAsync(
            new UpdateVmStatusCommand(vm.Id, "RequestFailed", Reason: "IPv6 could not be assigned."), default);
        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);

        Assert.Equal("IPv6 could not be assigned.", vm.RequestFailureReason);
        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            vm.Id, "cs-vm-abc", TenantProject, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotEqual(CurrentExistence.Deleted, vm.CurrentExistence);
    }

    // A VM the provider never runs would sit Creating for ever with no way out; once it has a provider id the
    // delete reaches the provider and cleans it up.
    [Fact]
    public async Task Delete_WhileTheCreateWaits_AfterTheProviderIdArrived_GoesToTheProvider()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Created", ExternalId: "cs-vm-abc"), default);

        await CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default);
        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Deleted"), default);

        _commands.Verify(w => w.EnqueueVmDeleteAsync(
            vm.Id, "cs-vm-abc", TenantProject, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new[] { "vm.delete_requested", "vm.deleted" }, _activity.Events.Select(e => e.Action));
    }

    [Fact] // no provider id yet: the create command may still instantiate, so there is nothing safe to delete
    public async Task Delete_WhileTheCreateWaits_WithoutAProviderId_IsRefused()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateDeleteHandler().HandleAsync(new DeleteVmCommand(vm.Id), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    [Fact] // the create report was lost, the give-up report carries the id
    public async Task UpdateStatus_RequestFailedCarryingTheId_KeepsTheProviderId()
    {
        Vm vm = MakeVm(VmSeed.Creating);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "RequestFailed", ExternalId: "cs-vm-abc"), default);

        Assert.NotNull(vm.RequestFailedAt);
        Assert.Equal("cs-vm-abc", vm.ExternalId);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
    }

    [Fact] // a guest-OS shutdown is not the outcome of anyone's request
    public async Task UpdateStatus_StoppedWithoutARequest_RecordsNothing()
    {
        var vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateUpdateStatusHandler().HandleAsync(new UpdateVmStatusCommand(vm.Id, "Observed", true, "Stopped", ExternalId: null, IpAddress: null), default);

        Assert.Empty(_activity.Events);
    }

    // ── The worker reports that a stop/start gave up ──

    [Fact]
    public async Task IntentFailed_OnAWaitingStop_FreesTheVm_AndRecordsStopFailedOnce()
    {
        SetupCurrentUser(TenantId);
        Vm vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);
        Guid intentId = Assert.IsType<Guid>(vm.PendingIntentId);
        _activity.Events.Clear();

        await CreateMarkIntentFailedHandler().HandleAsync(new MarkVmIntentFailedCommand(vm.Id, intentId), default);
        await CreateMarkIntentFailedHandler().HandleAsync(new MarkVmIntentFailedCommand(vm.Id, intentId), default);

        Assert.Null(vm.PendingIntent);
        var e = Assert.Single(_activity.Events);
        Assert.Equal("vm.stop_failed", e.Action);
        Assert.Equal(TenantId, e.OutcomeTenantId); // the VM's tenant: a callback has no user
        Assert.Equal(vm.Id, e.ResourceId);
        Assert.False(e.Saved);
    }

    [Fact]
    public async Task IntentFailed_OnAWaitingStart_RecordsStartFailed()
    {
        SetupCurrentUser(TenantId);
        Vm vm = MakeVm(VmSeed.Stopped);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);
        Guid intentId = Assert.IsType<Guid>(vm.PendingIntentId);
        _activity.Events.Clear();

        await CreateMarkIntentFailedHandler().HandleAsync(new MarkVmIntentFailedCommand(vm.Id, intentId), default);

        Assert.Equal("vm.start_failed", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task Start_AfterAFailedStop_IsANoOpOnARunningVm_AndStopIsAcceptedAgain()
    {
        SetupCurrentUser(TenantId);
        Vm vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);
        Guid firstIntentId = Assert.IsType<Guid>(vm.PendingIntentId);
        await CreateMarkIntentFailedHandler().HandleAsync(new MarkVmIntentFailedCommand(vm.Id, firstIntentId), default);

        await CreateStartHandler().HandleAsync(new StartVmCommand(vm.Id), default);
        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);

        Guid secondIntentId = Assert.IsType<Guid>(vm.PendingIntentId);
        Assert.NotEqual(firstIntentId, secondIntentId);
        _commands.Verify(w => w.EnqueueVmStopAsync(
            vm.Id, secondIntentId, "cs-vm-id", Tenant.ProjectNameFor(vm.TenantId), "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IntentFailed_ForAnOlderRequest_ChangesNothing_AndSavesNothing()
    {
        SetupCurrentUser(TenantId);
        Vm vm = MakeVm(VmSeed.Running);
        _repo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);
        await CreateStopHandler().HandleAsync(new StopVmCommand(vm.Id), default);
        _activity.Events.Clear();
        _repo.Invocations.Clear();

        await CreateMarkIntentFailedHandler().HandleAsync(new MarkVmIntentFailedCommand(vm.Id, Guid.NewGuid()), default);

        Assert.Equal(VmIntent.Stop, vm.PendingIntent);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IntentFailed_UnknownVm_ThrowsNotFound()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Vm?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateMarkIntentFailedHandler().HandleAsync(new MarkVmIntentFailedCommand(Guid.NewGuid(), Guid.NewGuid()), default));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // The tenant SetupTenant built; every provider call carries its Incus project
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#project-name).
    private Tenant? _tenant;
    private string TenantProject => _tenant!.ProjectName;

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    private void SetupTenant(int maxVms = 10, decimal? debtCeiling = null)
    {
        var tenant = TestTenant.Active("acme", "owner-1", "admin@acme.com", maxVms: maxVms);
        if (debtCeiling.HasValue)
            tenant.UpdateDebtCeiling((long)debtCeiling.Value);
        _tenant = tenant;
        _tenantRepo.Setup(r => r.FindByIdAsync(TenantId, default)).ReturnsAsync(tenant);
    }

    private static Network MakeActiveNetwork()
    {
        var network = Network.CreateUnchecked(TenantId, "net-1", "cluster-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    // What a test starts from: an Incus status, or a shape with none (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#existence-axis).
    public enum VmSeed { Creating, CreatedNotSeen, Running, Ready, Stopped, Frozen, Error, NotFound, CreateFailed, Deleted }

    private static Vm MakeVm(VmSeed seed)
    {
        var vm = MakeVmForTenant(TenantId);
        switch (seed)
        {
            case VmSeed.Creating: break; // Create() lands here: no instance, the create waiting
            case VmSeed.CreatedNotSeen: vm.MarkCreated("cs-vm-id"); break;
            case VmSeed.CreateFailed: vm.MarkRequestFailed("VM provisioning failed due to a system error.", "raw"); break;
            case VmSeed.Running: vm.MarkRunning("cs-vm-id", "10.0.0.2"); break;
            case VmSeed.Ready: vm.MarkRunning("cs-vm-id", "10.0.0.2"); vm.MarkObserved(VmState.Ready, "10.0.0.2"); break;
            case VmSeed.Stopped: vm.MarkRunning("cs-vm-id", "10.0.0.2"); vm.MarkStopped(); break;
            case VmSeed.Frozen: vm.MarkRunning("cs-vm-id", "10.0.0.2"); vm.MarkObserved(VmState.Frozen, "10.0.0.2"); break;
            case VmSeed.Error: vm.MarkRunning("cs-vm-id", "10.0.0.2"); vm.MarkObserved(VmState.Error, null); break;
            case VmSeed.NotFound: vm.MarkRunning("cs-vm-id", "10.0.0.2"); vm.MarkNotFound(); break;
            case VmSeed.Deleted: vm.MarkRunning("cs-vm-id", "10.0.0.2"); vm.RequestDelete(); vm.MarkDeleted(); break;
        }
        return vm;
    }

    private static Vm MakeVmForTenant(Guid tenantId) =>
        Vm.CreateUnchecked(tenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);

    private CreateVmCommand MakeCreateCommand() =>
        new("web-01", Guid.NewGuid(), "offering-1", "tmpl-1", "cluster-1", null);
}
