using Moq;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The request routes of titan-ops architecture/resource-lifecycle.md#callback-routes for a volume and its snapshots, at the
// handler: a report ends the request it names and records its outcome once; an observation writes current and ends
// nothing, but for the temporary exception pinned at the end of this file. The old route's mapping onto the same shape is
// in VolumeServiceTests and VolumeSnapshotServiceTests (UpdateStatus_*).
public class VolumeRequestHandlerTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    private readonly Mock<IVolumeRepository> _repo = new();
    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly Vm _vm = RunningVm();

    public VolumeRequestHandlerTests()
    {
        _vmRepo.Setup(r => r.ListByExternalIdsAsync(Owner.Id, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<string> names, CancellationToken _) =>
                names.Contains(_vm.ExternalId!) ? [_vm] : []);
    }

    private EndVolumeRequestCommandHandler EndHandler() => new(_repo.Object, _vmRepo.Object, _activity);
    private ObserveVolumeCommandHandler ObserveHandler() => new(_repo.Object, _vmRepo.Object, _activity);
    private EndVolumeSnapshotRequestCommandHandler EndSnapshotHandler() => new(_repo.Object, _activity);
    private ObserveVolumeSnapshotCommandHandler ObserveSnapshotHandler() => new(_repo.Object);

    private static Vm RunningVm()
    {
        Vm vm = Vm.Create(Owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm-ext", "10.0.0.5");
        return vm;
    }

    private Volume Given(Volume volume)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(volume.Id, It.IsAny<CancellationToken>())).ReturnsAsync(volume);
        return volume;
    }

    private static Volume NewVolume() => Volume.Create(Owner.Id, "data-1", "cluster-a", "std", 20);

    private static Volume RestingVolume()
    {
        Volume volume = NewVolume();
        volume.MarkCreated("vol-ext");
        return volume;
    }

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    // ---------- the volume's request report ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheId_RecordsVolumeCreatedOnce_AndSaves()
    {
        Volume volume = Given(NewVolume());

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, volume.Pending!.Id, "Done", "vol-ext"));

        Assert.Null(volume.Pending);
        Assert.Equal(("vol-ext", CurrentExistence.Exists), (volume.ExternalId, volume.CurrentExistence));
        Assert.Equal(["volume.created"], Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheCreate_WithoutTheVolumeName_Is422_AndSavesNothing()
    {
        Volume volume = Given(NewVolume());

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, volume.Pending!.Id, "Done")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.NotNull(volume.Pending);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Done_OfAnAttach_ResolvesTheInstanceToTheVm_AndRecordsVolumeAttachedOnce()
    {
        Volume volume = Given(RestingVolume());
        VolumeAttachProviderIds attach = volume.Attach(_vm);

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, attach.OperationId, "Done", AttachedTo: ["vm-ext"]));

        Assert.Null(volume.Pending);
        Assert.Equal(_vm.Id, volume.CurrentAttachedVmId);
        Assert.False(volume.CurrentAttachedElsewhere);
        Assert.Equal(["volume.attached"], Outcomes());
    }

    [Theory]
    [InlineData("Attach", "volume.attach_failed")]
    [InlineData("Detach", "volume.detach_failed")]
    [InlineData("Delete", "volume.delete_failed")]
    public async Task Failed_PutsDesiredBack_RecordsTheReason_AndTheFailedOutcomeOnce(string kind, string outcome)
    {
        Volume volume = Given(RestingVolume());
        Guid requestId;
        List<string> attachedTo = [];
        switch (kind)
        {
            case "Attach":
                requestId = volume.Attach(_vm).OperationId;
                break;
            case "Detach":
                VolumeAttachProviderIds attach = volume.Attach(_vm);
                volume.EndOperation(attach.OperationId, VolumeOperationEnd.Done(null, new VolumeObservation(_vm.Id, false)));
                requestId = volume.Detach().OperationId;
                attachedTo = ["vm-ext"];
                break;
            default:
                requestId = volume.Delete()!.OperationId;
                break;
        }

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, requestId, "Failed",
            AttachedTo: kind == "Delete" ? null : attachedTo, Reason: "The volume request could not be completed.", Detail: "raw"));

        Assert.Null(volume.Pending);
        Assert.Equal(DesiredExistence.Exists, volume.DesiredExistence);
        Assert.Equal(volume.CurrentAttachedVmId, volume.DesiredAttachedVmId);
        Assert.Equal(("The volume request could not be completed.", "raw"), (volume.OperationFailureReason, volume.OperationFailureDetail));
        Assert.Equal([outcome], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsAVolumeIncusMade_AndRecordsNoOutcome()
    {
        Volume volume = Given(NewVolume());

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, volume.Pending!.Id, "Failed", "vol-ext",
            Reason: "Volume creation failed.", Detail: "raw"));

        Assert.Equal(("vol-ext", CurrentExistence.Exists), (volume.ExternalId, volume.CurrentExistence));
        Assert.Equal("Volume creation failed.", volume.OperationFailureReason);
        Assert.Empty(Outcomes());
    }

    [Fact]
    public async Task Done_OfTheDelete_RecordsVolumeDeleted_AndTheSnapshotsWhoseDeleteWaited()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot leaving = volume.AddSnapshot("leaving").Snapshot;
        volume.MarkSnapshotCreated(leaving.Id, "vol-ext/s1");
        volume.DeleteSnapshot(leaving.Id);
        OperationTicket delete = volume.Delete()!;

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, delete.OperationId, "Done"));

        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.Equal(["volume.deleted", "volume_snapshot.deleted"], Outcomes().OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(leaving.Id, _activity.Events.Single(e => e.Action == "volume_snapshot.deleted").ResourceId);
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Volume volume = Given(RestingVolume());
        OperationTicket delete = volume.Delete()!;

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, Guid.NewGuid(), "Failed", Reason: "stale"));

        Assert.Equal(delete.OperationId, volume.Pending!.Id);
        Assert.Null(volume.OperationFailureReason);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_WithAnUnknownOutcome_Is400_BeforeTheVolumeIsRead()
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() =>
            EndHandler().HandleAsync(new EndVolumeRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Maybe")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_ForAVolumeThatIsNotThere_Is404()
    {
        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            EndHandler().HandleAsync(new EndVolumeRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done")));

        Assert.Equal("VOLUME_NOT_FOUND", refused.Code);
    }

    // ---------- the reconciler's observation ----------

    [Fact]
    public async Task AnObservation_WritesCurrent_AndLeavesADeleteWaiting()
    {
        Volume volume = Given(RestingVolume());
        OperationTicket delete = volume.Delete()!;

        await ObserveHandler().HandleAsync(new ObserveVolumeCommand(volume.Id, false));

        Assert.Equal(CurrentExistence.NotFound, volume.CurrentExistence);
        Assert.Equal(delete.OperationId, volume.Pending!.Id);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task AnObservation_OfAnInstanceOutsideTitan_IsAttachedElsewhere_AndDrift()
    {
        Volume volume = Given(RestingVolume());

        await ObserveHandler().HandleAsync(new ObserveVolumeCommand(volume.Id, true, ["not-a-titan-vm"]));

        Assert.True(volume.CurrentAttachedElsewhere);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Pending);
        Assert.Empty(_activity.Events);
    }

    // TEMPORARY, rollout phase 6 removes it (VolumeReports.EndAttachOrDetachTheObservationReached): an observation showing
    // what a waiting attach asked for ends it, once; the command's own Done arriving later is ignored.
    [Fact]
    public async Task AnObservation_ReachingAWaitingAttach_EndsIt_AndTheLaterDone_IsIgnored()
    {
        Volume volume = Given(RestingVolume());
        VolumeAttachProviderIds attach = volume.Attach(_vm);

        await ObserveHandler().HandleAsync(new ObserveVolumeCommand(volume.Id, true, ["vm-ext"]));
        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, attach.OperationId, "Done", AttachedTo: ["vm-ext"]));

        Assert.Null(volume.Pending);
        Assert.Equal(["volume.attached"], Outcomes());
    }

    // ---------- a snapshot's request report and observation ----------

    [Fact]
    public async Task Done_OfASnapshotCreate_StoresTheId_AndRecordsVolumeSnapshotCreatedOnce()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;

        await EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Pending!.Id, "Done", "vol-ext/s1"));

        Assert.Null(snapshot.Pending);
        Assert.Equal("vol-ext/s1", snapshot.ExternalId);
        Assert.Equal(["volume_snapshot.created"], Outcomes());
    }

    [Fact]
    public async Task Done_OfASnapshotCreate_WithoutTheSnapshotId_Is422_AndSavesNothing()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;

        var refused = await Assert.ThrowsAsync<BusinessException>(() => EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Pending!.Id, "Done")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.NotNull(snapshot.Pending);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_OfASnapshotDelete_GivesItBack_AndRecordsTheFailedOutcomeOnce()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;
        volume.MarkSnapshotCreated(snapshot.Id, "vol-ext/s1");
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;

        await EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, delete.OperationId, "Failed", Reason: "could not"));

        Assert.Null(snapshot.Pending);
        Assert.Equal(DesiredExistence.Exists, snapshot.DesiredExistence);
        Assert.Equal(["volume_snapshot.delete_failed"], Outcomes());
    }

    [Fact]
    public async Task ASnapshotReport_UnderAnotherVolume_Is404()
    {
        Volume volume = Given(RestingVolume());

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, Guid.NewGuid(), Guid.NewGuid(), "Done", "x")));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", refused.Code);
    }

    [Fact]
    public async Task ASnapshotObservation_WritesCurrent_AndLeavesTheCreateWaiting()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;

        await ObserveSnapshotHandler().HandleAsync(new ObserveVolumeSnapshotCommand(volume.Id, snapshot.Id, false));

        Assert.Equal(CurrentExistence.NotFound, snapshot.CurrentExistence);
        Assert.Equal(VolumeSnapshotOperationKind.Create, snapshot.Pending!.Kind);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
