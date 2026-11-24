using System.Globalization;
using Titan.Application.Services.Admin.Backup;
using Titan.Domain.Aggregates.BackupRecord;

namespace Titan.Tests.Services;

// Which archive a restore reads (titan-ops features/backup/design.md#restore-as-new-resource): the one the admin named,
// and for each volume a VM brings along, the one taken closest in time to the VM's archive, a day away at most.
public class RestoreArchivesTests
{
    private static readonly DateTimeOffset VmTakenAt = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly string Volume = Guid.NewGuid().ToString();

    private static string KeyOf(DateTimeOffset takenAt) => takenAt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    // A volume's archive, taken this long before (negative) or after the VM's.
    private static BackupRecord Archive(TimeSpan fromTheVms, BackupRecordStatus status = BackupRecordStatus.Complete)
    {
        DateTimeOffset takenAt = VmTakenAt + fromTheVms;
        return BackupRecord.Create(BackupRecord.VolumeKind, Volume, KeyOf(takenAt), takenAt, 1024, status, Guid.NewGuid());
    }

    private static BackupRecord[] Archives(params double[] hoursFromTheVms) =>
        hoursFromTheVms.Select(h => Archive(TimeSpan.FromHours(h))).ToArray();

    private static double? HoursFromTheVms(BackupRecord? archive)
    {
        if (archive == null)
        {
            return null;
        }
        return (archive.TakenAt - VmTakenAt).TotalHours;
    }

    // ---- The archive that goes with a VM's.

    [Theory]
    [InlineData(new[] { -5.0 }, -5.0)]
    [InlineData(new[] { 5.0 }, 5.0)]
    [InlineData(new[] { -3.0, 2.0 }, 2.0)]
    [InlineData(new[] { -1.0, 2.0 }, -1.0)]
    [InlineData(new[] { -23.0, -22.0, -2.0, 6.0, 20.0 }, -2.0)]
    [InlineData(new[] { 20.0, 6.0, -2.0, -22.0, -23.0 }, -2.0)]
    public void TheClosestArchive_IsTheOneTakenNearestInTime_BeforeOrAfter(double[] hours, double expected)
    {
        Assert.Equal(expected, HoursFromTheVms(RestoreArchives.ClosestTo(Archives(hours), VmTakenAt)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void AnArchiveExactlyADayAway_GoesWithTheVm_OneSecondMore_DoesNot(int sign)
    {
        TimeSpan aDay = TimeSpan.FromHours(24) * sign;
        TimeSpan aSecondMore = aDay + TimeSpan.FromSeconds(1) * sign;

        Assert.NotNull(RestoreArchives.ClosestTo([Archive(aDay)], VmTakenAt));
        Assert.Null(RestoreArchives.ClosestTo([Archive(aSecondMore)], VmTakenAt));
    }

    // The closest is too far: one further away is not looked for.
    [Fact]
    public void WhenEveryArchiveIsMoreThanADayAway_ThereIsNone()
    {
        Assert.Null(RestoreArchives.ClosestTo(Archives(-72, -48, 25, 49), VmTakenAt));
    }

    [Theory]
    [InlineData(new[] { -2.0, 2.0 })]
    [InlineData(new[] { 2.0, -2.0 })]
    public void OfTwoArchivesEquallyFar_TheEarlierOneIsTaken(double[] hours)
    {
        Assert.Equal(-2.0, HoursFromTheVms(RestoreArchives.ClosestTo(Archives(hours), VmTakenAt)));
    }

    [Fact]
    public void OnlyACompleteArchiveCounts()
    {
        BackupRecord incompleteAtTheSameMoment = Archive(TimeSpan.Zero, BackupRecordStatus.Incomplete);
        BackupRecord completeThreeHoursBefore = Archive(TimeSpan.FromHours(-3));

        Assert.Null(RestoreArchives.ClosestTo([incompleteAtTheSameMoment, Archive(TimeSpan.FromHours(1), BackupRecordStatus.Incomplete)], VmTakenAt));
        Assert.Same(completeThreeHoursBefore,
            RestoreArchives.ClosestTo([incompleteAtTheSameMoment, completeThreeHoursBefore], VmTakenAt));
    }

    [Fact]
    public void WithNoArchive_ThereIsNone()
    {
        Assert.Null(RestoreArchives.ClosestTo([], VmTakenAt));
    }

    // ---- The archive the admin named.

    [Fact]
    public void TheArchiveUnderAKey_IsTheCompleteOneThatCarriesIt()
    {
        BackupRecord[] archives = Archives(-48, -24, 0);

        Assert.Same(archives[1], RestoreArchives.WithKey(archives, KeyOf(VmTakenAt.AddHours(-24))));
        Assert.Null(RestoreArchives.WithKey(archives, KeyOf(VmTakenAt.AddHours(-12))));
        Assert.Null(RestoreArchives.WithKey([], KeyOf(VmTakenAt)));
    }

    [Fact]
    public void AnIncompleteArchive_IsNotOneToRestore()
    {
        BackupRecord incomplete = Archive(TimeSpan.Zero, BackupRecordStatus.Incomplete);

        Assert.Null(RestoreArchives.WithKey([incomplete], incomplete.BackupKey));
    }
}
