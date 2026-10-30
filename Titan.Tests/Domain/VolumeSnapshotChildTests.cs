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
    private static readonly Guid TenantId = Guid.NewGuid();

    private static Volume RestingVolume()
    {
        var volume = Volume.CreateUnchecked(TenantId, "vol-1", "cluster-1", "offering-1", 50);
        volume.MarkCreated("vol11");
        return volume;
    }

    private static Tenant TenantWithSnapshotCap(int maxVolumeSnapshotsPerVolume)
    {
        var tenant = TestTenant.Active("t", "kc", "t@x.io");
        tenant.UpdateQuota(TenantQuota.Create(10, 1, 10, 3, maxVolumeSnapshotsPerVolume, 20, 10, 20, 5));
        return tenant;
    }

    // The same five situations the repository count once measured (titan-ops features/volume/design.md#counting-and-billing):
    // creating and held by Incus count; a delete in flight, a snapshot Incus lost, a Deleted row and a failed create do not.
    private static Volume VolumeWithEverySnapshotSituation()
    {
        var volume = RestingVolume();
        volume.AddSnapshotUnchecked("creating"); // counts
        volume.AddSnapshotUnchecked("ready").MarkCreated("vol11/s1"); // counts
        var leaving = volume.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vol11/s2");
        leaving.RequestDelete();
        var lost = volume.AddSnapshotUnchecked("lost");
        lost.MarkCreated("vol11/s3");
        lost.MarkNotFound();
        var gone = volume.AddSnapshotUnchecked("gone");
        gone.MarkCreated("vol11/s4");
        gone.RequestDelete();
        gone.MarkDeleted();
        volume.AddSnapshotUnchecked("failed").MarkRequestFailed("could not create", "raw");
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
    public void AddSnapshot_AtTheCap_Refuses_AndAddsNothing()
    {
        var volume = VolumeWithEverySnapshotSituation(); // usage 2

        var ex = Assert.Throws<BusinessException>(() => volume.AddSnapshot(TenantWithSnapshotCap(2), "one-more"));

        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
        Assert.Equal(6, volume.Snapshots.Count);
    }

    [Fact]
    public void AddSnapshot_OneBelowTheCap_AddsASnapshotWaitingForItsCreate()
    {
        var volume = VolumeWithEverySnapshotSituation(); // usage 2

        VolumeSnapshot snapshot = volume.AddSnapshot(TenantWithSnapshotCap(3), "nightly");

        Assert.Contains(snapshot, volume.Snapshots);
        Assert.Equal(volume.Id, snapshot.VolumeId);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal(VolumeSnapshotTransition.Creating, snapshot.Transition);
        Assert.Equal(3, volume.SnapshotQuotaUsage);
    }

    // The volume says it cannot take a snapshot before anyone counts: a different code from the cap.
    [Fact]
    public void AddSnapshot_OnAVolumeNotResting_IsRefusedBeforeTheQuota()
    {
        var volume = Volume.CreateUnchecked(TenantId, "vol-1", "cluster-1", "offering-1", 50); // Creating

        var ex = Assert.Throws<BusinessException>(() => volume.AddSnapshot(TenantWithSnapshotCap(0), "nightly"));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Empty(volume.Snapshots);
    }

    // The volume's delete took its snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots).
    // Only a snapshot whose own delete was waiting is named: its request is done here, once.
    [Fact]
    public void MarkDeleted_MarksEverySnapshotDeleted_AndNamesOnlyTheSnapshotsWhoseOwnDeleteWasWaiting()
    {
        var volume = RestingVolume();
        var lost = volume.AddSnapshotUnchecked("lost");
        lost.MarkCreated("vol11/s1");
        lost.MarkNotFound();
        var leaving = volume.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vol11/s2");
        leaving.RequestDelete();
        var alreadyGone = volume.AddSnapshotUnchecked("gone");
        alreadyGone.MarkCreated("vol11/s3");
        alreadyGone.RequestDelete();
        alreadyGone.MarkDeleted();
        volume.RequestDelete(); // lost, leaving and gone do not count, so the delete is accepted

        VolumeDeletionOutcome outcome = volume.MarkDeleted();

        Assert.True(outcome.FulfilledVolumeDelete);
        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.All(volume.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.Equal([leaving.Id], outcome.SnapshotsWhoseDeleteWasFulfilled.Select(s => s.Id));
    }

    [Fact]
    public void MarkDeleted_WithNoSnapshots_NamesNone()
    {
        var volume = RestingVolume();
        volume.RequestDelete();

        VolumeDeletionOutcome outcome = volume.MarkDeleted();

        Assert.True(outcome.FulfilledVolumeDelete);
        Assert.Empty(outcome.SnapshotsWhoseDeleteWasFulfilled);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    [Fact]
    public void MarkDeleted_Repeated_IsAbsorbed_AndNamesNothingAgain()
    {
        var volume = RestingVolume();
        var leaving = volume.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vol11/s2");
        leaving.RequestDelete();
        volume.RequestDelete();
        volume.MarkDeleted();

        VolumeDeletionOutcome repeat = volume.MarkDeleted();

        Assert.False(repeat.FulfilledVolumeDelete);
        Assert.Empty(repeat.SnapshotsWhoseDeleteWasFulfilled);
    }

    // A snapshot the volume does not have answers like a missing one.
    [Fact]
    public void RequireSnapshot_OfAnotherVolumesSnapshot_IsNotFound()
    {
        var volume = RestingVolume();
        var other = RestingVolume();
        var foreign = other.AddSnapshotUnchecked("theirs");
        volume.AddSnapshotUnchecked("mine");

        Assert.Null(volume.SnapshotOrNull(foreign.Id));
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.RequireSnapshot(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.RequestSnapshotDelete(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.BeginSnapshotRestore(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.MarkSnapshotDeleted(foreign.Id)).Code);
    }

    // The root routes to the snapshot and returns what the snapshot returns.
    [Fact]
    public void SnapshotCalls_ReachTheSnapshotNamed_AndReturnItsAnswer()
    {
        var volume = RestingVolume();
        var snapshot = volume.AddSnapshotUnchecked("s");
        var untouched = volume.AddSnapshotUnchecked("u");

        Assert.True(volume.MarkSnapshotCreated(snapshot.Id, "vol11/s1"));
        Assert.False(volume.MarkSnapshotObserved(snapshot.Id));
        Assert.Equal("vol11/s1", volume.BeginSnapshotRestore(snapshot.Id));

        Assert.Equal("vol11/s1", volume.RequestSnapshotDelete(snapshot.Id));
        Assert.Equal(VolumeSnapshotTransition.Deleting, snapshot.Transition);
        volume.AbandonSnapshotRequest(snapshot.Id, "refused", "raw");
        Assert.Null(snapshot.Transition);
        Assert.Equal("refused", snapshot.RequestFailureReason);

        volume.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(CurrentExistence.NotFound, snapshot.CurrentExistence);
        volume.RequestSnapshotDelete(snapshot.Id);
        Assert.True(volume.MarkSnapshotDeleted(snapshot.Id));

        Assert.Equal(VolumeSnapshotTransition.Creating, untouched.Transition);
        Assert.Null(untouched.ExternalId);
    }

    [Fact]
    public void DeleteSnapshotLocally_OnASnapshotWhoseCreateFailed_WritesBothPairs()
    {
        var volume = RestingVolume();
        var failed = volume.AddSnapshotUnchecked("failed");
        volume.MarkSnapshotRequestFailed(failed.Id, "reason", "detail");

        volume.DeleteSnapshotLocally(failed.Id);

        Assert.Equal(CurrentExistence.Deleted, failed.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.Equal(0, volume.SnapshotQuotaUsage);
    }
}
