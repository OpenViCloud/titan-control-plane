using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A volume snapshot is the volume's child entity (titan-ops architecture/code-structure.md#child-entity): the volume
// counts its snapshots toward the per-volume cap and toward "may this volume be deleted", routes each request and
// observation to the snapshot it names, and takes every snapshot with it when its own delete is confirmed.
public class VolumeSnapshotChildTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;

    private static Volume RestingVolume()
    {
        var volume = Volume.Create(TenantIdOwner.Id, "vol-1", "cluster-1", "offering-1", 50);
        volume.MarkCreated("vol11");
        return volume;
    }

    // The same five situations the repository count once measured (titan-ops features/volume/design.md#counting-and-billing):
    // creating and held by Incus count; a delete in flight, a snapshot Incus lost, a Deleted row and a failed create do not.
    private static Volume VolumeWithEverySnapshotSituation()
    {
        var volume = RestingVolume();
        volume.WithSnapshot("creating"); // counts
        volume.WithSnapshot("ready").MarkCreated("vol11/s1"); // counts
        var leaving = volume.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s2");
        leaving.Delete();
        var lost = volume.WithSnapshot("lost");
        lost.MarkCreated("vol11/s3");
        lost.MarkNotFound();
        var gone = volume.WithSnapshot("gone");
        gone.MarkCreated("vol11/s4");
        volume.EndSnapshotOperation(gone.Id, volume.DeleteSnapshot(gone.Id)!.OperationId, VolumeSnapshotOperationEnd.Done(null));
        volume.WithSnapshot("failed").MarkRequestFailed("could not create", "raw");
        return volume;
    }

    [Fact]
    public void SnapshotQuotaUsage_CountsCreatingAndExisting_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        var volume = VolumeWithEverySnapshotSituation();

        Assert.Equal(6, volume.Snapshots.Count);
        Assert.Equal(2, volume.SnapshotQuotaUsage);
    }

    [Fact]
    public void AddSnapshot_OneBelowTheCap_AddsASnapshotWaitingForItsCreate()
    {
        var volume = VolumeWithEverySnapshotSituation(); // usage 2

        VolumeSnapshot snapshot = volume.AddSnapshot("nightly").Snapshot;

        Assert.Contains(snapshot, volume.Snapshots);
        Assert.Equal(volume.Id, snapshot.VolumeId);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal(VolumeSnapshotTransition.Creating, snapshot.Transition);
        Assert.Equal(3, volume.SnapshotQuotaUsage);
    }

    [Fact]
    public void AddSnapshot_OnAVolumeNotResting_IsRefused()
    {
        var volume = Volume.Create(TenantIdOwner.Id, "vol-1", "cluster-1", "offering-1", 50); // Creating

        var ex = Assert.Throws<BusinessException>(() => volume.AddSnapshot("nightly"));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Empty(volume.Snapshots);
    }

    // The volume's delete Done took its snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots).
    // Only a snapshot whose own delete was waiting is named: its request is done here, once.
    [Fact]
    public void MarkDeleted_MarksEverySnapshotDeleted_AndNamesOnlyTheSnapshotsWhoseOwnDeleteWasWaiting()
    {
        var volume = RestingVolume();
        var lost = volume.WithSnapshot("lost");
        lost.MarkCreated("vol11/s1");
        volume.MarkSnapshotNotFound(lost.Id);
        var leaving = volume.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s2");
        volume.DeleteSnapshot(leaving.Id);
        var alreadyGone = volume.WithSnapshot("gone");
        alreadyGone.MarkCreated("vol11/s3");
        volume.EndSnapshotOperation(alreadyGone.Id, volume.DeleteSnapshot(alreadyGone.Id)!.OperationId,
            VolumeSnapshotOperationEnd.Done(null));
        OperationTicket delete = volume.Delete()!; // lost, leaving and gone do not count, so the delete is accepted

        VolumeOperationEnded? ended = volume.EndOperation(delete.OperationId, VolumeOperationEnd.Done(null, null));

        Assert.Equal(VolumeOperationKind.Delete, ended!.Operation.Kind);
        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.All(volume.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.All(volume.Snapshots, s => Assert.Null(s.Pending));
        Assert.Equal([leaving.Id], ended.SnapshotsWhoseDeleteEnded.Select(s => s.Id));
    }

    [Fact]
    public void MarkDeleted_WithNoSnapshots_NamesNone()
    {
        var volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;

        VolumeOperationEnded? ended = volume.EndOperation(delete.OperationId, VolumeOperationEnd.Done(null, null));

        Assert.NotNull(ended);
        Assert.Empty(ended.SnapshotsWhoseDeleteEnded);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs): the same Done again
    // ends nothing, so no snapshot's delete is recorded twice.
    [Fact]
    public void MarkDeleted_Repeated_IsAbsorbed_AndNamesNothingAgain()
    {
        var volume = RestingVolume();
        var leaving = volume.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s2");
        volume.DeleteSnapshot(leaving.Id);
        OperationTicket delete = volume.Delete()!;
        volume.EndOperation(delete.OperationId, VolumeOperationEnd.Done(null, null));

        VolumeOperationEnded? repeat = volume.EndOperation(delete.OperationId, VolumeOperationEnd.Done(null, null));

        Assert.Null(repeat);
    }

    // A snapshot the volume does not have answers like a missing one.
    [Fact]
    public void ASnapshotOfAnotherVolume_IsNotFound()
    {
        var volume = RestingVolume();
        var other = RestingVolume();
        var foreign = other.WithSnapshot("theirs");
        volume.WithSnapshot("mine");

        Assert.DoesNotContain(volume.Snapshots, s => s.Id == foreign.Id);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.DeleteSnapshot(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.ProviderIdForSnapshotRestore(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => volume.EndSnapshotOperation(foreign.Id, foreign.Pending!.Id, VolumeSnapshotOperationEnd.Done("vol11/s1"))).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.MarkSnapshotNotFound(foreign.Id)).Code);
    }

    // The root routes to the snapshot and returns what the snapshot returns.
    [Fact]
    public void SnapshotCalls_ReachTheSnapshotNamed_AndReturnItsAnswer()
    {
        var volume = RestingVolume();
        var snapshot = volume.WithSnapshot("s");
        var untouched = volume.WithSnapshot("u");
        Guid createRequestId = snapshot.Pending!.Id;

        Assert.Equal(createRequestId,
            volume.EndSnapshotOperation(snapshot.Id, createRequestId, VolumeSnapshotOperationEnd.Done("vol11/s1"))!.Id);
        volume.MarkSnapshotObserved(snapshot.Id);
        Assert.Equal("vol11/s1", volume.ProviderIdForSnapshotRestore(snapshot.Id));

        OperationTicket refused = volume.DeleteSnapshot(snapshot.Id)!;
        Assert.Equal("vol11/s1", refused.ProviderId);
        Assert.Equal(VolumeSnapshotTransition.Deleting, snapshot.Transition);
        volume.EndSnapshotOperation(snapshot.Id, refused.OperationId, VolumeSnapshotOperationEnd.Failed(null, "refused", "raw"));
        Assert.Null(snapshot.Transition);
        Assert.Equal("refused", snapshot.OperationFailureReason);

        volume.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(CurrentExistence.NotFound, snapshot.CurrentExistence);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;
        Assert.Equal(delete.OperationId,
            volume.EndSnapshotOperation(snapshot.Id, delete.OperationId, VolumeSnapshotOperationEnd.Done(null))!.Id);
        Assert.Equal(CurrentExistence.Deleted, snapshot.CurrentExistence);

        Assert.Equal(VolumeSnapshotTransition.Creating, untouched.Transition);
        Assert.Null(untouched.ExternalId);
    }

    [Fact]
    public void RequestSnapshotDelete_OfASnapshotWhoseCreateFailed_WritesBothPairs_WithNoTicket()
    {
        var volume = RestingVolume();
        var failed = volume.WithSnapshot("failed");
        volume.EndSnapshotOperation(failed.Id, failed.Pending!.Id, VolumeSnapshotOperationEnd.Failed(null, "reason", "detail"));

        Assert.Null(volume.DeleteSnapshot(failed.Id));

        Assert.Equal(CurrentExistence.Deleted, failed.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.Equal(0, volume.SnapshotQuotaUsage);
    }
}
