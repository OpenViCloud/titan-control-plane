using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Queries.Compute.VolumeSnapshot.Contracts;
using VolumeCmd = Titan.Application.Commands.Compute.Volume;
using VolSnapQry = Titan.Application.Queries.Compute.VolumeSnapshot;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;

namespace Titan.Tests.Services;

// https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#tenant-scoping — another tenant's resource answers 404, never 403:
// a 403 confirms the resource exists and only the permission is missing. Every ownership check in
// Compute has to agree, because the same volume is reachable through more than one path.
public class ComputeOwnershipTests
{
    private static readonly Guid CallerTenant = Guid.NewGuid();
    private static readonly Guid OtherTenant = Guid.NewGuid();

    private readonly Mock<IVolumeRepository> _volumeRepo = new();
    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly Mock<IVmSnapshotRepository> _vmSnapshotRepo = new();
    private readonly Mock<IVolumeSnapshotRepository> _volumeSnapshotRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Titan.Tests.Fakes.FakeActivityRecorder _activity = new();

    // RequireTenantId is an extension over ICurrentUser.TenantId, so the property is what gets
    // stubbed — Moq cannot intercept the extension method itself.
    public ComputeOwnershipTests() =>
        _currentUser.Setup(c => c.TenantId).Returns(CallerTenant);

    private VolumeCmd.DeleteVolumeCommandHandler DeleteVolume() =>
        new(_volumeRepo.Object, _volumeSnapshotRepo.Object, _currentUser.Object, _commands.Object, _activity);

    private VolumeCmd.DetachVolumeCommandHandler DetachVolume() =>
        new(_volumeRepo.Object, _currentUser.Object, _commands.Object, _activity);

    private VolumeCmd.AttachVolumeCommandHandler AttachVolume() =>
        new(_volumeRepo.Object, _vmRepo.Object, _currentUser.Object, _commands.Object, _activity);

    private VolSnapQry.ListVolumeSnapshotsQueryHandler ListVolumeSnapshots() =>
        new(_volumeSnapshotRepo.Object, _volumeRepo.Object, _currentUser.Object);

    private static Volume ForeignVolume() =>
        Volume.CreateUnchecked(OtherTenant, "vol-of-someone-else", "cluster-1", "offering-1", 50);

    // ── The leak itself ───────────────────────────────────────────────────
    //
    // ForbiddenException maps to 403 in Program.cs; each path must throw NotFoundException instead.

    [Fact]
    public async Task Delete_VolumeOfAnotherTenant_Answers404NotForbidden()
    {
        var volume = ForeignVolume();
        _volumeRepo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await Assert.ThrowsAsync<NotFoundException>(
            () => DeleteVolume().HandleAsync(new DeleteVolumeCommand(volume.Id), default));
    }

    [Fact]
    public async Task Detach_VolumeOfAnotherTenant_Answers404NotForbidden()
    {
        var volume = ForeignVolume();
        _volumeRepo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await Assert.ThrowsAsync<NotFoundException>(
            () => DetachVolume().HandleAsync(new DetachVolumeCommand(volume.Id), default));
    }

    [Fact]
    public async Task Attach_VolumeOfAnotherTenant_Answers404NotForbidden()
    {
        var volume = ForeignVolume();
        _volumeRepo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        await Assert.ThrowsAsync<NotFoundException>(
            () => AttachVolume().HandleAsync(new AttachVolumeCommand(volume.Id, Guid.NewGuid()), default));
    }

    // ── The asymmetry: the SAME volume, reached two ways ──────────────────
    //
    // ⚠️ A 403 inside ComputeOwnership.RequireVolumeAsync leaves this test green, because both paths
    // call that one method. It guards against a future split: if either path gets its own ownership
    // check again, this goes red. The tests around it are what prove the answer is 404.

    [Fact]
    public async Task SameForeignVolume_AnswersTheSameWayThroughBothServices()
    {
        var volume = ForeignVolume();
        _volumeRepo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        var throughVolumeService = await Record.ExceptionAsync(
            () => DeleteVolume().HandleAsync(new DeleteVolumeCommand(volume.Id), default));
        var throughSnapshotService = await Record.ExceptionAsync(
            () => ListVolumeSnapshots().HandleAsync(new ListVolumeSnapshotsQuery(volume.Id), default));

        Assert.NotNull(throughVolumeService);
        Assert.NotNull(throughSnapshotService);
        Assert.Equal(throughSnapshotService!.GetType(), throughVolumeService!.GetType());
    }

    // ── The code must stay the same too ───────────────────────────────────
    //
    // 404 with a code that names the resource type is what the customer already sees; the error code
    // is part of the contract, not only the status.

    [Fact]
    public async Task Delete_VolumeOfAnotherTenant_KeepsVolumeNotFoundCode()
    {
        var volume = ForeignVolume();
        _volumeRepo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => DeleteVolume().HandleAsync(new DeleteVolumeCommand(volume.Id), default));

        Assert.Equal("VOLUME_NOT_FOUND", ex.Code);
    }

    // ── A missing resource and a foreign one must be indistinguishable ────
    //
    // The whole point of answering 404 for a foreign resource is that it reads exactly like a
    // resource that was never there. Seeding a DIFFERENT id (not the foreign one) keeps this from
    // being a tautology against the test above.

    [Fact]
    public async Task ForeignVolumeAndMissingVolume_AreIndistinguishable()
    {
        var foreign = ForeignVolume();
        var missingId = Guid.NewGuid();
        _volumeRepo.Setup(r => r.FindByIdAsync(foreign.Id, default)).ReturnsAsync(foreign);
        _volumeRepo.Setup(r => r.FindByIdAsync(missingId, default)).ReturnsAsync((Volume?)null);

        var foreignEx = await Assert.ThrowsAsync<NotFoundException>(
            () => DeleteVolume().HandleAsync(new DeleteVolumeCommand(foreign.Id), default));
        var missingEx = await Assert.ThrowsAsync<NotFoundException>(
            () => DeleteVolume().HandleAsync(new DeleteVolumeCommand(missingId), default));

        Assert.Equal(missingEx.Code, foreignEx.Code);
        Assert.Equal(missingEx.Message, foreignEx.Message);
    }
}
