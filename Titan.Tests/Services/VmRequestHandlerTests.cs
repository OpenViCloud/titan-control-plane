using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Domain;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The two request routes of titan-ops architecture/resource-lifecycle.md#callback-routes at the handler: a report ends the
// request it names and records its outcome once; an observation writes current and ends nothing, but for the temporary exception pinned at the end of this file. The old routes'
// mapping onto the same shape is in VmServiceTests (UpdateStatus_* and IntentFailed_*).
public class VmRequestHandlerTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    private readonly Mock<IVmRepository> _repo = new();
    private readonly Mock<IVolumeRepository> _volumeRepo = new();
    private readonly FakeActivityRecorder _activity = new();

    public VmRequestHandlerTests()
    {
        _volumeRepo.Setup(r => r.ListAttachedToVmAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Volume>());
    }

    private EndVmRequestCommandHandler EndHandler() => new(_repo.Object, _volumeRepo.Object, _activity);
    private ObserveVmCommandHandler ObserveHandler() => new(_repo.Object, _volumeRepo.Object, _activity);

    private Vm Given(Vm vm)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private static Vm NewVm() => Vm.Create(Owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm RunningVm()
    {
        Vm vm = NewVm();
        vm.MarkRunning("42", "10.0.0.5");
        return vm;
    }

    private static VmObservationReport Seen(string status, string? ip = null) => new(status, ip);

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    // ---------- EndRequest: the report names the request ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheId_RecordsVmCreatedOnce_AndSaves()
    {
        Vm vm = Given(NewVm());
        Guid createId = vm.Pending!.Id;

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, createId, "Done", "42", Seen("Running", "10.0.0.5")));

        Assert.Null(vm.Pending);
        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal(["vm.created"], Outcomes());
        Assert.Equal(Owner.Id, _activity.Events.Single().OutcomeTenantId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheCreate_WithoutTheInstanceName_Is400_AndChangesNothing()
    {
        Vm vm = Given(NewVm());

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, vm.Pending!.Id, "Done")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.NotNull(vm.Pending);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Stop", "Stopped", "vm.stopped")]
    [InlineData("Start", "Running", "vm.started")]
    public async Task Done_OfAStopOrStart_EndsIt_AndRecordsTheOutcomeOnce(string kind, string seen, string outcome)
    {
        Vm vm = Given(RunningVm());
        if (kind == "Start") vm.MarkStopped();
        RequestTicket ticket = kind == "Stop" ? vm.RequestStop() : vm.RequestStart();

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, ticket.RequestId, "Done", null, Seen(seen)));
        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, ticket.RequestId, "Done", null, Seen(seen)));

        Assert.Null(vm.Pending);
        Assert.Equal(seen, vm.CurrentState.ToString());
        Assert.Equal([outcome], Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once); // the repeat ends nothing and saves nothing
    }

    [Fact]
    public async Task Done_OfADelete_EndsTheVm_RecordsVmDeleted_TheSnapshotsWhoseDeleteWaited_AndReleasesItsVolumes()
    {
        Vm vm = Given(RunningVm());
        VmSnapshot leaving = vm.WithSnapshot("leaving");
        leaving.MarkCreated("42/s1");
        vm.RequestSnapshotDelete(leaving.Id);
        vm.WithSnapshot("resting").MarkCreated("42/s2");
        Volume volume = Volume.Create(Owner.Id, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        volume.Attach(vm);
        volume.MarkObserved(vm.Id, false);
        _volumeRepo.Setup(r => r.ListAttachedToVmAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync([volume]);
        RequestTicket delete = vm.RequestDelete()!;

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, delete.RequestId, "Done"));

        Assert.True(vm.IsTerminal);
        Assert.All(vm.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.Equal(["vm.deleted", "vm_snapshot.deleted"], Outcomes().OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(leaving.Id, _activity.Events.Single(e => e.Action == "vm_snapshot.deleted").ResourceId);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    [Fact]
    public async Task Failed_OfADelete_LeavesItsVolumeAttached_WithNothingWaiting_AndANewDeleteCanTakeIt()
    {
        Vm vm = Given(RunningVm());
        Volume volume = Volume.Create(Owner.Id, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        volume.Attach(vm);
        volume.MarkObserved(vm.Id, false);
        _volumeRepo.Setup(r => r.ListAttachedToVmAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync([volume]);
        RequestTicket delete = vm.RequestDelete()!;
        volume.DetachWithDeletedVm();

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, delete.RequestId, "Failed", Reason: "It failed."));

        Assert.Null(volume.Transition);
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
        Assert.NotNull(vm.RequestDelete());
        volume.DetachWithDeletedVm();
        Assert.Equal(VolumeTransition.Detaching, volume.Transition);
    }

    [Theory]
    [InlineData("Delete", "vm.delete_failed")]
    [InlineData("Stop", "vm.stop_failed")]
    public async Task Failed_OfADeleteOrStop_PutsDesiredBack_RecordsTheReason_AndTheFailedOutcomeOnce(string kind, string outcome)
    {
        Vm vm = Given(RunningVm());
        RequestTicket ticket = kind == "Delete" ? vm.RequestDelete()! : vm.RequestStop();

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, ticket.RequestId, "Failed", Reason: "The VM could not be changed.", Detail: "raw"));

        Assert.Null(vm.Pending);
        Assert.Equal(DesiredExistence.Exists, vm.DesiredExistence);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Equal("The VM could not be changed.", vm.RequestFailureReason);
        Assert.Equal("raw", vm.RequestFailureDetail);
        Assert.Equal([outcome], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsAnInstanceIncusMade_AndRecordsNoOutcome()
    {
        Vm vm = Given(NewVm());

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, vm.Pending!.Id, "Failed", "45", null, "VM provisioning failed.", "raw"));

        Assert.Null(vm.Pending);
        Assert.Equal("45", vm.ExternalId);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal("VM provisioning failed.", vm.RequestFailureReason);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Vm vm = Given(RunningVm());
        RequestTicket stop = vm.RequestStop();

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, Guid.NewGuid(), "Done", null, Seen("Stopped")));
        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, Guid.NewGuid(), "Failed", Reason: "stale"));

        Assert.Equal(stop.RequestId, vm.Pending!.Id);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Null(vm.RequestFailureReason);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_WhenNothingWaits_OrOnADeletedVm_IsIgnored()
    {
        Vm resting = Given(RunningVm());
        await EndHandler().HandleAsync(new EndVmRequestCommand(resting.Id, Guid.NewGuid(), "Done", null, Seen("Stopped")));
        Assert.Equal(VmState.Running, resting.CurrentState);

        Vm deleted = Given(RunningVm());
        RequestTicket delete = deleted.RequestDelete()!;
        deleted.MarkDeleted();
        await EndHandler().HandleAsync(new EndVmRequestCommand(deleted.Id, delete.RequestId, "Done", null, Seen("Running")));
        Assert.True(deleted.IsTerminal);
        Assert.Null(deleted.CurrentState);

        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Finished", null)]
    [InlineData("Done", "Sleeping")]
    public async Task AMalformedReport_Is400_BeforeTheVmIsRead(string outcome, string? status)
    {
        Vm vm = Given(RunningVm());
        VmObservationReport? observed = status == null ? null : Seen(status);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, Guid.NewGuid(), outcome, null, observed)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_ForAnUnknownVm_Is404()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            EndHandler().HandleAsync(new EndVmRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done")));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ---------- Observe: current only ----------

    [Fact]
    public async Task AnObservation_WritesCurrent_AndLeavesTheCreateWaiting()
    {
        Vm vm = Given(NewVm());
        Guid createId = vm.Pending!.Id;

        await ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, true, Seen("Running", "10.0.0.5")));

        Assert.Equal(createId, vm.Pending!.Id);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal("10.0.0.5", vm.IpAddress);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotFound_LeavesAWaitingDeleteWaiting()
    {
        Vm vm = Given(RunningVm());
        RequestTicket delete = vm.RequestDelete()!;

        await ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, false));

        Assert.Equal(delete.RequestId, vm.Pending!.Id);
        Assert.Equal(CurrentExistence.NotFound, vm.CurrentExistence);
        Assert.Empty(_activity.Events);
    }

    // TEMPORARY, rollout phase 6 removes it (VmReports.EndStopOrStartTheObservationReached).
    [Fact]
    public async Task AnObservation_ReachingAWaitingStop_EndsIt_AndTheLaterDone_IsIgnored()
    {
        Vm vm = Given(RunningVm());
        RequestTicket stop = vm.RequestStop();

        await ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, true, Seen("Stopped")));
        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, stop.RequestId, "Done", null, Seen("Stopped")));

        Assert.Null(vm.Pending);
        Assert.Equal(["vm.stopped"], Outcomes());
    }

    [Fact]
    public async Task AnObservation_OfAnExistingInstance_WithoutItsStatus_Is400()
    {
        Vm vm = Given(RunningVm());

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, true, null)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
