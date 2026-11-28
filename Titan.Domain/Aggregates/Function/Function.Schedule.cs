namespace Titan.Domain.Aggregates.Function;

public partial class Function
{
    // The bounds of the five fields, in order: minute, hour, day of month, month, day of week (0 is Sunday; the CronJob
    // API refuses 7, "end of range (7) above maximum (6)", measured on Kubernetes 1.37).
    private static readonly (int Min, int Max)[] ScheduleFields = [(0, 59), (0, 23), (1, 31), (1, 12), (0, 6)];

    /// A cron schedule of exactly five fields, one space between two, read in UTC
    /// (titan-ops features/ovc-function/design.md#function-limits): each field is `*` or a comma list of values and
    /// ranges `a-b`, either with an optional `/step`. No names (JAN, MON), no macros (@hourly), no `?`, `L`, `W` or `#`:
    /// only what every cron reads the same way.
    public static bool IsASchedule(string? schedule)
    {
        if (string.IsNullOrEmpty(schedule))
        {
            return false;
        }
        string[] fields = schedule.Split(' ');
        if (fields.Length != ScheduleFields.Length || schedule.Any(c => c != ' ' && !IsScheduleChar(c)))
        {
            return false;
        }
        for (int i = 0; i < fields.Length; i++)
        {
            if (!IsAScheduleField(fields[i], ScheduleFields[i].Min, ScheduleFields[i].Max))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsScheduleChar(char c) => c is (>= '0' and <= '9') or '*' or ',' or '-' or '/';

    private static bool IsAScheduleField(string field, int min, int max)
    {
        (string body, bool stepOk) = SplitStep(field, max);
        if (!stepOk)
        {
            return false;
        }
        if (body == "*")
        {
            return true;
        }
        foreach (string item in body.Split(','))
        {
            if (!IsAScheduleItem(item, min, max))
            {
                return false;
            }
        }
        return true;
    }

    // `body/step`, the step 1 to max; a field without one is its own body.
    private static (string Body, bool StepOk) SplitStep(string field, int max)
    {
        int slash = field.IndexOf('/');
        if (slash < 0)
        {
            return (field, true);
        }
        string step = field[(slash + 1)..];
        bool stepOk = TryScheduleNumber(step, out int n) && n >= 1 && n <= max;
        return (field[..slash], stepOk);
    }

    private static bool IsAScheduleItem(string item, int min, int max)
    {
        string[] ends = item.Split('-');
        if (ends.Length == 1)
        {
            return TryScheduleNumber(ends[0], out int value) && value >= min && value <= max;
        }
        if (ends.Length != 2)
        {
            return false;
        }
        if (!TryScheduleNumber(ends[0], out int from) || !TryScheduleNumber(ends[1], out int to))
        {
            return false;
        }
        return from >= min && to <= max && from <= to;
    }

    // One or two ASCII digits: 007 is not a minute.
    private static bool TryScheduleNumber(string text, out int value)
    {
        value = 0;
        if (text.Length is < 1 or > 2 || !text.All(char.IsAsciiDigit))
        {
            return false;
        }
        value = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}
