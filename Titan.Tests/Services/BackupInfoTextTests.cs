using Titan.Application.Commands.Platform.Backup;

namespace Titan.Tests.Services;

// backup.info is barman's key=value text. The real file of the dev bucket was not downloaded (1.45 kB, listed only), so these
// pin the two keys the status reads and how it behaves on a file that says something else.
public class BackupInfoTextTests
{
    [Theory]
    [InlineData("backup_label=None\nstatus=DONE\nsize=10\n", true)]
    [InlineData("status=DONE", true)]
    [InlineData("status = DONE \r\n", true)]
    [InlineData("status=STARTED\n", false)]
    [InlineData("status=FAILED\n", false)]
    [InlineData("status=done\n", false)]
    [InlineData("", false)]
    [InlineData("nothing here", false)]
    [InlineData("xstatus=DONE\n", false)]
    public void IsDone_OnlyWhenStatusIsDone(string text, bool expected)
    {
        Assert.Equal(expected, BackupInfoText.IsDone(text));
    }

    [Fact]
    public void EndTime_ParsesBarmansTimestamp_WithItsOffset()
    {
        var time = BackupInfoText.EndTime("begin_time=2026-10-24 14:06:31.123456+00:00\nend_time=2026-10-24 14:06:33.654321+00:00\n");

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 6, 33, TimeSpan.Zero).AddTicks(6543210), time);
    }

    [Fact]
    public void EndTime_WithoutAnOffset_IsReadAsUtc()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 6, 33, TimeSpan.Zero), BackupInfoText.EndTime("end_time=2026-10-24 14:06:33"));
    }

    [Theory]
    [InlineData("end_time=\n")]
    [InlineData("end_time=garbage\n")]
    [InlineData("status=DONE\n")]
    public void EndTime_IsNull_WhenMissingOrNotATime(string text)
    {
        Assert.Null(BackupInfoText.EndTime(text));
    }
}
