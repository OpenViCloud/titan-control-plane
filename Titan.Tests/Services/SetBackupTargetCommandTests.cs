using Titan.Application.Commands.Platform.BackupTarget.Contracts;

namespace Titan.Tests.Services;

// The secret is write-only: it must not come back out of the command through the text a log line or a test failure prints.
public class SetBackupTargetCommandTests
{
    [Fact]
    public void ToString_NeverPrintsTheSecret_ButKeepsTheRest()
    {
        var command = new SetBackupTargetCommand(
            "https://acct.r2.cloudflarestorage.com", "auto", "titan-backup-dev", "key-id", "S3CR3T-value-0123456789");

        string text = command.ToString();

        Assert.DoesNotContain("S3CR3T-value-0123456789", text);
        Assert.Contains("titan-backup-dev", text);
        Assert.Contains("key-id", text);
    }

    [Fact]
    public void ToString_WithoutASecret_StillPrintsNoSecretField()
    {
        var command = new SetBackupTargetCommand("https://acct.r2.cloudflarestorage.com", "auto", "b", "key-id", null);

        Assert.Contains("***", command.ToString());
    }
}
