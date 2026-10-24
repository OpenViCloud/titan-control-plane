using System.Globalization;

namespace Titan.Application.Commands.Platform.Backup;

// barman's backup.info is lines of key=value. Only the two keys the status needs are read, leniently: a file that does not
// say status=DONE is an unfinished backup, and a missing or odd end_time falls back to the object's own LastModified.
public static class BackupInfoText
{
    public static bool IsDone(string text) => Value(text, "status") == "DONE";

    public static DateTimeOffset? EndTime(string text)
    {
        string? raw = Value(text, "end_time");
        bool parsed = DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset time);
        return parsed ? time : null;
    }

    private static string? Value(string text, string key)
    {
        foreach (string line in text.Split('\n'))
        {
            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim() == key)
            {
                return line[(equals + 1)..].Trim();
            }
        }
        return null;
    }
}
