using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Queries.Compute.Volume.Contracts;
using VolumeCmd = Titan.Application.Commands.Compute.Volume;
using VolumeQry = Titan.Application.Queries.Compute.Volume;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class VolumeServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    // The tenant's Incus project, as titan-ops architecture/tenancy-and-admin.md#project-name spells it.
    private static string Project => "t-" + TenantId.ToString("N");

    private readonly Mock<IVolumeRepository> _repo = new();
    private readonly Mock<IVmRepository> _vmRepo = new();
    // Moq answers 0 to an unset count: no live snapshot unless a test says so.
    private readonly Mock<IVolumeSnapshotRepository> _snapshotRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Titan.Tests.Fakes.FakeActivityRecorder _activity = new();
    // The catalog holds the offering MakeCreateRequest names; a test that wants an unknown one asks for it.
    private readonly FakeVolumeOfferingRepository _offerings = SeededOfferings();

    private static FakeVolumeOfferingRepository SeededOfferings()
    {
        var offerings = new FakeVolumeOfferingRepository();
        offerings.Seed(Titan.Domain.Aggregates.Catalog.VolumeOffering.VolumeOffering.Create(
            "offering-1", "Standard", 50, false, 1.5m));
        return offerings;
    }

    // Facade over the six volume handlers so the tests keep one call shape; every method forwards to
    // exactly one handler and adds nothing.
    private VolumeFacade CreateService() => new(
        _repo.Object, _offerings, _snapshotRepo.Object, _vmRepo.Object, _tenantRepo.Object, _currentUser.Object, _commands.Object, _activity);

    private static readonly Titan.Application.Options.ClusterOptions Clusters = new Titan.Application.Options.ClusterOptions { Clusters = [new() { Id = "cluster-1", Name = "Cluster 1" }] };

    private sealed class VolumeFacade(
        IVolumeRepository repo,
        Titan.Domain.Repositories.Catalog.IVolumeOfferingRepository offeringRepo,
        IVolumeSnapshotRepository snapshotRepo,
        IVmRepository vmRepo,
        ITenantRepository tenantRepo,
        ICurrentUser currentUser,
        IProviderCommandQueue commands,
        IActivityRecorder activity)
    {
        public Task<CreateVolumeCommandResult> CreateAsync(CreateVolumeCommand req, CancellationToken ct = default)
            => new VolumeCmd.CreateVolumeCommandHandler(repo, offeringRepo, tenantRepo, currentUser, commands, activity, Clusters)
                .HandleAsync(req, ct);

        public Task AttachAsync(Guid volumeId, AttachVolumeBody body, CancellationToken ct = default)
            => new VolumeCmd.AttachVolumeCommandHandler(repo, vmRepo, currentUser, commands, activity)
                .HandleAsync(new AttachVolumeCommand(volumeId, body.VmId), ct);

        public Task DetachAsync(Guid volumeId, CancellationToken ct = default)
            => new VolumeCmd.DetachVolumeCommandHandler(repo, currentUser, commands, activity)
                .HandleAsync(new DetachVolumeCommand(volumeId), ct);

        public Task DeleteAsync(Guid volumeId, CancellationToken ct = default)
            => new VolumeCmd.DeleteVolumeCommandHandler(repo, snapshotRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteVolumeCommand(volumeId), ct);

        public Task<List<VolumeQueryResult>> ListAsync(CancellationToken ct = default)
            => new VolumeQry.ListVolumesQueryHandler(repo, currentUser).HandleAsync(new ListVolumesQuery(), ct);

        public Task UpdateStatusAsync(Guid volumeId, UpdateVolumeStatusBody body, CancellationToken ct = default)
            => new VolumeCmd.UpdateVolumeStatusCommandHandler(repo, snapshotRepo, vmRepo, activity)
                .HandleAsync(new UpdateVolumeStatusCommand(volumeId, body.Report, body.Exists, body.AttachedTo,
                    body.ExternalId, body.Reason, body.Detail), ct);
    }

    // Test-local request bodies, not contracts; the facade unpacks them into commands.
    private record AttachVolumeBody(Guid VmId);
    private record UpdateVolumeStatusBody(
        string Report, bool? Exists = null, List<string>? AttachedTo = null, string? ExternalId = null,
        string? Reason = null, string? Detail = null);

    // ── CreateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Success_ReturnsACreatingVolume()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var result = await CreateService().CreateAsync(MakeCreateRequest(), default);

        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so the
        // starting status is asserted on the volume that was actually added.
        Assert.NotEqual(Guid.Empty, result.VolumeId);
        _repo.Verify(r => r.Add(It.Is<Volume>(v => v.Id == result.VolumeId
                                                && v.Transition == VolumeTransition.Creating)), Times.Once);
        _repo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVolumeCreateAsync(
            It.IsAny<Guid>(), "vol-01", "cluster-1", 50,
            "t-" + _tenant!.Id.ToString("N"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A volume Incus holds without a price stops the disk meter (titan-ops features/volume/design.md#offering-must-exist).
    [Fact]
    public async Task Create_UnknownOffering_Is404_AndNothingIsWritten()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(new CreateVolumeCommand("vol-01", "no-such-offering", "cluster-1", 50), default));

        Assert.Equal("VOLUME_OFFERING_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Volume>()), Times.Never);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVolumeCreateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The offering id is a key, not a name: the display name in another case is still unknown.
    [Fact]
    public async Task Create_OfferingIdInAnotherCase_Is404()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(new CreateVolumeCommand("vol-01", "Offering-1", "cluster-1", 50), default));

        Assert.Equal("VOLUME_OFFERING_NOT_FOUND", ex.Code);
    }

    // A cluster Titan does not have must not fall through to a default cluster
    // (titan-ops features/catalog/design.md#cluster-is-a-real-choice).
    [Fact]
    public async Task Create_UnknownCluster_ThrowsNotFound_AndEnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService().CreateAsync(new CreateVolumeCommand("vol-01", "offering-1", "nowhere", 50), default));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
        _repo.Verify(r => r.Add(It.IsAny<Volume>()), Times.Never);
        Assert.Empty(_commands.Invocations);
    }

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(10);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(MakeCreateRequest(), default));

        Assert.Equal("VOLUME_QUOTA_EXCEEDED", ex.Code);
    }

    [Fact]
    public async Task Create_NoTenant_ThrowsForbiddenException()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().CreateAsync(MakeCreateRequest(), default));

        Assert.Equal("NOT_A_MEMBER", ex.Code);
    }

    // ── AttachAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Attach_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, externalVolumeId: "cs-vol-1");
        var vm = MakeVm(VmState.Running, clusterId: volume.ClusterId, externalVmId: "cs-vm-1");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default);

        Assert.Equal(VolumeTransition.Attaching, volume.Transition);
        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
        _commands.Verify(w => w.EnqueueVolumeAttachAsync(
            volume.Id, "cs-vol-1", "cs-vm-1", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Attach_VolumeNotAvailable_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Attaching);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        // Volume.Attach(vm) takes the VM, so the VM lookup comes first and a missing VM wins with 404.
        // Seeding a real VM isolates the rule this test is named after instead of leaning on error
        // precedence.
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-1", 20);
        vm.MarkRunning("ext-vm", "10.0.0.5");
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    [Fact]
    public async Task Attach_VmNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Vm?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().AttachAsync(volume.Id, new AttachVolumeBody(Guid.NewGuid()), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Attach_VmBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available);
        var vm = MakeVm(VmState.Running, tenantId: Guid.NewGuid());
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Attach_ClusterMismatch_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, clusterId: "cluster-a");
        var vm = MakeVm(VmState.Running, clusterId: "cluster-b");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default));

        Assert.Equal("VOLUME_CLUSTER_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task Attach_VmInErrorState_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available);
        var vm = MakeVm(VmState.Error, clusterId: volume.ClusterId);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    // ── DetachAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Detach_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Attached, externalVolumeId: "cs-vol-2");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await CreateService().DetachAsync(volume.Id, default);

        Assert.Equal(VolumeTransition.Detaching, volume.Transition);
        _commands.Verify(w => w.EnqueueVolumeDetachAsync(
            volume.Id, "cs-vol-2", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    // A volume that is not attached is already what a detach asks for, so the request is done — no
    // row, no provider command.
    public async Task Detach_VolumeNotAttached_IsANoOp()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await CreateService().DetachAsync(volume.Id, default);

        Assert.Empty(_activity.Events);
        _commands.Verify(w => w.EnqueueVolumeDetachAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // a repeat attach to the same VM succeeds like the first, without a second command
    public async Task Attach_RepeatedToTheSameVm_Succeeds_WithoutASecondCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, externalVolumeId: "cs-vol-1");
        var vm = MakeVm(VmState.Running, clusterId: volume.ClusterId, externalVmId: "cs-vm-1");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default);
        await CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default);

        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
        Assert.Equal("volume.attach_requested", Assert.Single(_activity.Events).Action);
        _commands.Verify(w => w.EnqueueVolumeAttachAsync(
            volume.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, externalVolumeId: "cs-vol-3");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await CreateService().DeleteAsync(volume.Id, default);

        Assert.Equal(VolumeTransition.Deleting, volume.Transition);
        _commands.Verify(w => w.EnqueueVolumeDeleteAsync(
            volume.Id, "cs-vol-3", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory] // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers quietly
    [InlineData(VolumeSeed.Deleting)]
    [InlineData(VolumeSeed.Deleted)]
    public async Task Delete_RepeatedOrDone_IsANoOp_RecordsNoActivity_EnqueuesNoCommand(VolumeSeed from)
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(from, externalVolumeId: "cs-vol-3");
        string before = Shape(volume);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await CreateService().DeleteAsync(volume.Id, default);

        Assert.Equal(before, Shape(volume));
        Assert.Empty(_activity.Events);
        _commands.Verify(w => w.EnqueueVolumeDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_VolumeAttached_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Attached);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(volume.Id, default));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    // On Incus a volume's delete takes its snapshots with it (lab-storage 07): refused while one lives
    // (titan-ops features/volume/design.md#delete-refused-with-snapshots).
    [Fact]
    public async Task Delete_VolumeWithALiveSnapshot_IsRefused_RecordsNothing_SendsNothing()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, externalVolumeId: "cs-vol-3");
        string before = Shape(volume);
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _snapshotRepo.Setup(r => r.CountActiveByVolumeIdAsync(volume.Id, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateService().DeleteAsync(volume.Id, default));

        Assert.Equal("VOLUME_HAS_SNAPSHOTS", ex.Code);
        Assert.Equal(before, Shape(volume));
        Assert.Empty(_activity.Events);
        _commands.Verify(w => w.EnqueueVolumeDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_VolumeNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Volume?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(Guid.NewGuid(), default));

        Assert.Equal("VOLUME_NOT_FOUND", ex.Code);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsVolumesForTenant()
    {
        SetupCurrentUser(TenantId);
        var volumes = new List<Volume>
        {
            MakeVolume(VolumeSeed.Available),
            MakeVolume(VolumeSeed.Attached)
        };
        _repo.Setup(r => r.ListByTenantAsync(TenantId, default)).ReturnsAsync(volumes);

        var result = await CreateService().ListAsync(default);

        Assert.Equal(2, result.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // The tenant SetupTenant built; every provider call carries its Incus project
    // (titan-ops architecture/tenancy-and-admin.md#project-name).
    private Tenant? _tenant;

    // ── Activity recording ──────────────────────────────────────

    [Fact]
    public async Task Create_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        await CreateService().CreateAsync(MakeCreateRequest(), default);

        var e = Assert.Single(_activity.Events);
        Assert.Equal("volume.create_requested", e.Action);
        Assert.Equal("volume", e.ResourceType);
        Assert.Equal("vol-01", e.ResourceName);
    }

    [Fact]
    public async Task Attach_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, externalVolumeId: "cs-vol-1");
        var vm = MakeVm(VmState.Running, clusterId: volume.ClusterId, externalVmId: "cs-vm-1");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        await CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id), default);

        Assert.Equal("volume.attach_requested", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task Detach_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Attached, externalVolumeId: "cs-vol-2");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await CreateService().DetachAsync(volume.Id, default);

        Assert.Equal("volume.detach_requested", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task Delete_Success_RecordsActivity()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available, externalVolumeId: "cs-vol-3");
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await CreateService().DeleteAsync(volume.Id, default);

        Assert.Equal("volume.delete_requested", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task Create_QuotaExceeded_RecordsNoActivity()
    {
        SetupCurrentUser(TenantId);
        SetupTenant();
        _repo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(10);

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(MakeCreateRequest(), default));

        Assert.Empty(_activity.Events);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    private void SetupTenant()
    {
        var tenant = TestTenant.Active("tenant-1", "owner-keycloak-id", "t1@example.com");

        _tenant = tenant;
        _tenantRepo.Setup(r => r.FindByIdAsync(TenantId, default)).ReturnsAsync(tenant);
    }

    private static CreateVolumeCommand MakeCreateRequest() =>
        new("vol-01", "offering-1", "cluster-1", 50);

    // ── Callbacks (titan-ops features/volume/design.md#volume-reports) ─────────────

    // No snapshot of the volume is live unless a test says so: a Deleted report reads them.
    private void SetupVolume(Volume volume)
    {
        _repo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);
        _snapshotRepo.Setup(r => r.ListLiveOfVolumeForUpdateAsync(volume.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VolumeSnapshot>());
    }

    // The tenant's VMs the callback resolves instance names against (titan-ops features/volume/design.md#attachment-from-used-by).
    private void SetupTenantVms(params Vm[] vms) =>
        _vmRepo.Setup(r => r.ListByExternalIdsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid tenantId, IReadOnlyCollection<string> names, CancellationToken _) =>
                vms.Where(v => v.TenantId == tenantId && v.ExternalId != null && names.Contains(v.ExternalId)).ToList());

    [Theory] // titan-ops features/activity/design.md#outcome-source: each request's outcome, once, for the volume's tenant
    [InlineData(VolumeSeed.Creating, "volume.created")]
    [InlineData(VolumeSeed.Attaching, "volume.attached")]
    [InlineData(VolumeSeed.Detaching, "volume.detached")]
    [InlineData(VolumeSeed.Deleting, "volume.deleted")]
    public async Task UpdateStatus_CompletingARequest_RecordsItsOutcomeOnce(VolumeSeed from, string action)
    {
        Vm vm = MakeVm(VmState.Running, externalVmId: "vm-attached");
        var volume = MakeVolume(from, attachTo: vm);
        SetupVolume(volume);
        SetupTenantVms(vm);
        UpdateVolumeStatusBody report = from switch
        {
            VolumeSeed.Creating => new UpdateVolumeStatusBody("Created", ExternalId: "cs-vol-default"),
            VolumeSeed.Attaching => new UpdateVolumeStatusBody("Observed", Exists: true, AttachedTo: ["vm-attached"]),
            VolumeSeed.Detaching => new UpdateVolumeStatusBody("Observed", Exists: true, AttachedTo: []),
            _ => new UpdateVolumeStatusBody("Deleted"),
        };

        await CreateService().UpdateStatusAsync(volume.Id, report);
        await CreateService().UpdateStatusAsync(volume.Id, report);

        var e = Assert.Single(_activity.Events);
        Assert.Equal(action, e.Action);
        Assert.Equal(TenantId, e.OutcomeTenantId);
        Assert.Null(volume.Transition);
    }

    [Fact] // the reconciler lists only volumes with an id; seeing one whose Created report was lost ends its create
    public async Task UpdateStatus_ObservedOnAWaitingCreate_EndsIt_AndRecordsVolumeCreated()
    {
        var volume = MakeVolume(VolumeSeed.Creating);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody("Observed", Exists: true));

        Assert.Equal("Exists/Exists/-/-/-", Shape(volume));
        Assert.Equal("volume.created", Assert.Single(_activity.Events).Action);
    }

    // P3-1: an instance that is no Titan VM of this tenant — another tenant's VM of the same name included.
    [Fact]
    public async Task UpdateStatus_UsedByAnInstanceThatIsNoVmOfTheTenant_IsAttachedElsewhere()
    {
        var volume = MakeVolume(VolumeSeed.Available);
        SetupVolume(volume);
        Vm othersVm = MakeVm(VmState.Running, externalVmId: "vm-shared-name", tenantId: Guid.NewGuid());
        SetupTenantVms(othersVm);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("Observed", Exists: true, AttachedTo: ["vm-shared-name"]));

        Assert.Null(volume.CurrentAttachedVmId);
        Assert.True(volume.CurrentAttachedElsewhere);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task UpdateStatus_UsedByATitanVmAndAnInstanceOutsideTitan_IsBoth()
    {
        Vm vm = MakeVm(VmState.Running, externalVmId: "vm-mine");
        var volume = MakeVolume(VolumeSeed.Available);
        SetupVolume(volume);
        SetupTenantVms(vm);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("Observed", Exists: true, AttachedTo: ["foreign", "vm-mine"]));

        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
        Assert.True(volume.CurrentAttachedElsewhere);
    }

    [Fact] // attached outside Titan to a Titan VM, nothing waiting: current follows Incus, desired stays — drift, no outcome
    public async Task UpdateStatus_AttachedOutsideTitan_IsDrift_AndRecordsNothing()
    {
        Vm vm = MakeVm(VmState.Running, externalVmId: "vm-mine");
        var volume = MakeVolume(VolumeSeed.Available);
        SetupVolume(volume);
        SetupTenantVms(vm);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("Observed", Exists: true, AttachedTo: ["vm-mine"]));

        Assert.Equal("Exists/Exists/vm/-/-", Shape(volume));
        Assert.Empty(_activity.Events);
    }

    [Fact] // drift the other way: detached outside Titan
    public async Task UpdateStatus_DetachedOutsideTitan_IsDrift_AndRecordsNothing()
    {
        var volume = MakeVolume(VolumeSeed.Attached);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody("Observed", Exists: true, AttachedTo: []));

        Assert.Equal("Exists/Exists/-/vm/-", Shape(volume));
        Assert.Empty(_activity.Events);
    }

    // The attach command gave up, reporting what Incus showed: not attached, so the request is withdrawn with why.
    [Fact]
    public async Task UpdateStatus_AFailedAttach_WithdrawsTheRequest_WithTheReason_AndRecordsNothing()
    {
        var volume = MakeVolume(VolumeSeed.Attaching);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("RequestFailed", Exists: true, AttachedTo: [], Reason: "Attaching failed.", Detail: "raw"));

        Assert.Equal("Exists/Exists/-/-/-", Shape(volume));
        Assert.Equal("Attaching failed.", volume.RequestFailureReason);
        Assert.Empty(_activity.Events);
    }

    // The attach happened, only its report failed: the give-up's observation fulfils it, and nothing is withdrawn.
    [Fact]
    public async Task UpdateStatus_AGiveUpShowingTheAttachDone_IsTheAttachDone()
    {
        Vm vm = MakeVm(VmState.Running, externalVmId: "vm-attached");
        var volume = MakeVolume(VolumeSeed.Attaching, attachTo: vm);
        SetupVolume(volume);
        SetupTenantVms(vm);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("RequestFailed", Exists: true, AttachedTo: ["vm-attached"], Reason: "Attaching failed."));

        Assert.Equal("Exists/Exists/vm/vm/-", Shape(volume));
        Assert.Null(volume.RequestFailureReason);
        Assert.Equal("volume.attached", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task UpdateStatus_AFailedDetach_LeavesItAttached_WithTheReason()
    {
        Vm vm = MakeVm(VmState.Running, externalVmId: "vm-attached");
        var volume = MakeVolume(VolumeSeed.Detaching, attachTo: vm);
        SetupVolume(volume);
        SetupTenantVms(vm);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("RequestFailed", Exists: true, AttachedTo: ["vm-attached"], Reason: "Detaching failed."));

        Assert.Equal("Exists/Exists/vm/vm/-", Shape(volume));
        Assert.Equal("Detaching failed.", volume.RequestFailureReason);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task UpdateStatus_RequestFailed_OnAWaitingCreate_RecordsWhy_AndLeavesItNotCreated()
    {
        var volume = MakeVolume(VolumeSeed.Creating);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("RequestFailed", Reason: "Volume creation failed.", Detail: "raw"));

        Assert.Equal("NotCreated/Exists/-/-/-", Shape(volume));
        Assert.Equal("Volume creation failed.", volume.RequestFailureReason);
        Assert.Equal("raw", volume.RequestFailureDetail);
        Assert.Empty(_activity.Events);
    }

    [Fact] // the give-up found the volume on Incus: the create happened
    public async Task UpdateStatus_RequestFailedCarryingTheId_IsTheCreateDone()
    {
        var volume = MakeVolume(VolumeSeed.Creating);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("RequestFailed", ExternalId: "vol9", Reason: "Volume creation failed."));

        Assert.Equal("Exists/Exists/-/-/-", Shape(volume));
        Assert.Equal("vol9", volume.ExternalId);
        Assert.Equal("volume.created", Assert.Single(_activity.Events).Action);
    }

    [Fact]
    public async Task UpdateStatus_RequestFailed_WhileADeleteWaits_GivesTheVolumeBack_AndItCanBeDeletedAgain()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Deleting, externalVolumeId: "cs-vol-3");
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id,
            new UpdateVolumeStatusBody("RequestFailed", Reason: "Volume deletion failed.", Detail: "refused"));
        Assert.Equal("Exists/Exists/-/-/-", Shape(volume));
        Assert.Equal("Volume deletion failed.", volume.RequestFailureReason);

        await CreateService().DeleteAsync(volume.Id, default);

        Assert.Equal(VolumeTransition.Deleting, volume.Transition);
        _commands.Verify(w => w.EnqueueVolumeDeleteAsync(
            volume.Id, "cs-vol-3", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Only the delete command ends a delete: the reconciler not finding the volume is NotFound.
    [Fact]
    public async Task UpdateStatus_NotFound_WhileADeleteWaits_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        var volume = MakeVolume(VolumeSeed.Deleting);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody("Observed", Exists: false));

        Assert.Equal("NotFound/Deleted/-/-/Deleting", Shape(volume));
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task UpdateStatus_NotFound_OnAVolumeStillWanted_IsNotFound_AndItCanBeDeleted()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Attached);
        SetupVolume(volume);

        await CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody("Observed", Exists: false));

        Assert.Equal("NotFound/Exists/-/vm/-", Shape(volume));
        await CreateService().DeleteAsync(volume.Id, default);
        Assert.Equal(VolumeTransition.Deleting, volume.Transition);
    }

    // Incus deletes a volume's snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots): the
    // volume's Deleted report marks every one not yet Deleted, and records the outcome of a snapshot whose own delete
    // was waiting — its delete command will only find it already Deleted.
    [Fact]
    public async Task UpdateStatus_Deleted_MarksTheVolumesSnapshotsDeleted_AndRecordsAWaitingSnapshotDeleteOnly()
    {
        var volume = MakeVolume(VolumeSeed.Deleting);
        SetupVolume(volume);
        var lost = VolumeSnapshot.CreateUnchecked(volume.Id, TenantId, "lost");
        lost.MarkCreated("vol/s1");
        lost.MarkNotFound();
        var leaving = VolumeSnapshot.CreateUnchecked(volume.Id, TenantId, "leaving");
        leaving.MarkCreated("vol/s2");
        leaving.RequestDelete();
        _snapshotRepo.Setup(r => r.ListLiveOfVolumeForUpdateAsync(volume.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VolumeSnapshot> { lost, leaving });

        await CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody("Deleted"));

        Assert.True(lost.IsTerminal);
        Assert.True(leaving.IsTerminal);
        string[] actions = _activity.Events.Select(e => $"{e.Action}:{e.ResourceName}").OrderBy(a => a, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "volume.deleted:vol-01", "volume_snapshot.deleted:leaving" }, actions);
    }

    [Theory] // reports the worker never sends, the old words included
    [InlineData("Allocated")]
    [InlineData("Attached")]
    [InlineData("Absent")]
    [InlineData("Error")]
    [InlineData("Attaching")]
    public async Task UpdateStatus_UnknownReport_Is400_AndSavesNothing(string report)
    {
        var volume = MakeVolume(VolumeSeed.Available);
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody(report)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ObservedWithoutExists_Is400()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), new UpdateVolumeStatusBody("Observed")));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task UpdateStatus_VolumeNotFound_ThrowsNotFoundException()
    {
        _repo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Volume?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), new UpdateVolumeStatusBody("Deleted")));

        Assert.Equal("VOLUME_NOT_FOUND", ex.Code);
    }

    // ── Requests the callbacks left behind ──

    [Fact] // Incus made the volume before the create command answered: once its id arrived, the delete reaches Incus
    public async Task Delete_AfterTheCreateReportedItsId_GoesToIncus()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Creating);
        SetupVolume(volume);
        await CreateService().UpdateStatusAsync(volume.Id, new UpdateVolumeStatusBody("Created", ExternalId: "vol7"));

        await CreateService().DeleteAsync(volume.Id, default);

        _commands.Verify(w => w.EnqueueVolumeDeleteAsync(
            volume.Id, "vol7", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_WhileTheCreateWaits_IsRefused()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Creating);
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateService().DeleteAsync(volume.Id, default));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Empty(_commands.Invocations);
    }

    // A create that failed before Incus had the volume leaves the list at once, with no command
    // (titan-ops architecture/desired-and-current.md#delete-locally).
    [Fact]
    public async Task Delete_OfACreateThatFailedBeforeIncusHadIt_DeletesLocally_AndSendsNothing()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Creating);
        volume.MarkRequestFailed("Volume creation failed.", "raw");
        SetupVolume(volume);

        await CreateService().DeleteAsync(volume.Id, default);

        Assert.Equal("Deleted/Deleted/-/-/-", Shape(volume));
        Assert.Empty(_commands.Invocations);
        Assert.Equal(new[] { "volume.delete_requested", "volume.deleted" }, _activity.Events.Select(e => e.Action).ToArray());
    }

    // P3-1: Titan does not edit an instance it does not manage.
    [Fact]
    public async Task AttachDetachAndDelete_OfAVolumeAttachedOutsideTitan_AreRefused()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Available);
        volume.MarkObserved(null, true);
        SetupVolume(volume);
        var vm = MakeVm(VmState.Running, clusterId: volume.ClusterId);
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, default)).ReturnsAsync(vm);

        var attach = await Assert.ThrowsAsync<BusinessException>(() => CreateService().AttachAsync(volume.Id, new AttachVolumeBody(vm.Id)));
        var detach = await Assert.ThrowsAsync<BusinessException>(() => CreateService().DetachAsync(volume.Id));
        var delete = await Assert.ThrowsAsync<BusinessException>(() => CreateService().DeleteAsync(volume.Id));

        Assert.Equal("VOLUME_INVALID_STATE", attach.Code);
        Assert.Equal("VOLUME_INVALID_STATE", detach.Code);
        Assert.Equal("VOLUME_INVALID_STATE", delete.Code);
        Assert.Empty(_commands.Invocations);
    }

    // A volume still being created has a request waiting too — the create. The detach repeat check must not read that
    // as "a detach is already waiting" and answer 202 for a detach it never started.
    [Fact]
    public async Task Detach_WhileTheVolumeIsStillBeingCreated_IsRefused_NotTakenForARepeat()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolumeSeed.Creating);
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => CreateService().DetachAsync(volume.Id, default));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    // The situations a volume can be in, in the tests' vocabulary: Attaching/Detaching/Deleting mean "that request is
    // waiting".
    public enum VolumeSeed { Creating, Available, Attaching, Attached, Detaching, Deleting, Deleted }

    private static string Shape(Volume v)
    {
        string current = "-";
        if (v.CurrentAttachedVmId != null)
        {
            current = "vm";
        }
        string desired = "-";
        if (v.DesiredAttachedVmId != null)
        {
            desired = "vm";
        }
        string transition = "-";
        if (v.Transition != null)
        {
            transition = v.Transition.Value.ToString();
        }
        return $"{v.CurrentExistence}/{v.DesiredExistence}/{current}/{desired}/{transition}";
    }

    private static Volume MakeVolume(
        VolumeSeed status,
        string? clusterId = null,
        string? externalVolumeId = null,
        Guid? tenantId = null,
        Vm? attachTo = null)
    {
        var vol = Volume.CreateUnchecked(tenantId ?? TenantId, "vol-01", clusterId ?? "cluster-1", "offering-1", 50);
        var externalId = externalVolumeId ?? "cs-vol-default";
        Vm target = attachTo ?? MakeVm(VmState.Running, clusterId: clusterId, tenantId: tenantId);
        switch (status)
        {
            case VolumeSeed.Creating:
                break;
            case VolumeSeed.Available:
                vol.MarkCreated(externalId);
                break;
            case VolumeSeed.Attaching:
                vol.MarkCreated(externalId);
                vol.Attach(target);
                break;
            case VolumeSeed.Attached:
                vol.MarkCreated(externalId);
                vol.Attach(target);
                vol.MarkObserved(target.Id, false);
                break;
            case VolumeSeed.Detaching:
                vol.MarkCreated(externalId);
                vol.Attach(target);
                vol.MarkObserved(target.Id, false);
                vol.Detach();
                break;
            case VolumeSeed.Deleting:
                vol.MarkCreated(externalId);
                vol.RequestDelete(liveSnapshotCount: 0);
                break;
            case VolumeSeed.Deleted:
                vol.MarkCreated(externalId);
                vol.RequestDelete(liveSnapshotCount: 0);
                vol.MarkDeleted();
                break;
        }
        return vol;
    }

    private static Vm MakeVm(
        VmState status,
        string? clusterId = null,
        string? externalVmId = null,
        Guid? tenantId = null)
    {
        var vm = Vm.CreateUnchecked(
            tenantId ?? TenantId,
            Guid.NewGuid(),
            "test-vm",
            "offering-1",
            "tmpl-1",
            clusterId ?? "cluster-1",
            0);

        switch (status)
        {
            case VmState.Running:
                vm.MarkRunning(externalVmId ?? "cs-vm-1", "10.0.0.1");
                break;
            case VmState.Error:
                vm.MarkRunning(externalVmId ?? "cs-vm-1", "10.0.0.1");
                vm.MarkObserved(VmState.Error, null);
                break;
        }

        return vm;
    }

    // The raw error of a give-up is admin-only (titan-ops architecture/desired-and-current.md#request-failure); Incus has
    // no status word for a volume (titan-ops features/volume/design.md#volume-state-is-existence).
    [Fact]
    public void TenantVolumeQueryResult_HasNoFailureDetailField_NorAStateWord()
    {
        Assert.Null(typeof(VolumeQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(VolumeQueryResult).GetProperty("CurrentState"));
        Assert.Null(typeof(VolumeQueryResult).GetProperty("DesiredState"));
        Assert.Null(typeof(VolumeQueryResult).GetProperty("AttachedVmId"));
        Assert.NotNull(typeof(VolumeQueryResult).GetProperty("RequestFailureReason"));
        Assert.NotNull(typeof(VolumeQueryResult).GetProperty("CurrentExistence"));
        Assert.NotNull(typeof(VolumeQueryResult).GetProperty("CurrentAttachedElsewhere"));
    }
}
