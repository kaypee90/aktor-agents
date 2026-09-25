namespace AgentRuntime.Workspaces;

/// <summary>
/// A standard five-field cron expression (minute hour day-of-month month day-of-week), evaluated in
/// UTC. Supports <c>*</c>, lists (<c>1,15</c>), ranges (<c>1-5</c>), steps (<c>*/15</c>, <c>0-30/10</c>)
/// and day-of-week 0-7 (0 and 7 are Sunday). As in classic cron, when both day-of-month and
/// day-of-week are restricted, a day matching either one fires.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    public string Expression { get; }

    private CronSchedule(string expression)
    {
        Expression = expression;
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            throw new FormatException("A cron expression needs five fields: minute hour day-of-month month day-of-week.");
        }

        Fill(_minutes, fields[0], 0, 59, "minute");
        Fill(_hours, fields[1], 0, 23, "hour");
        Fill(_daysOfMonth, fields[2], 1, 31, "day-of-month");
        Fill(_months, fields[3], 1, 12, "month");

        var dow = new bool[8];
        Fill(dow, fields[4], 0, 7, "day-of-week");
        for (var i = 0; i < 7; i++) _daysOfWeek[i] = dow[i];
        if (dow[7]) _daysOfWeek[0] = true;

        _domRestricted = fields[2] != "*";
        _dowRestricted = fields[4] != "*";
    }

    public static CronSchedule Parse(string expression) => new(expression.Trim());

    public static bool TryParse(string expression, out CronSchedule? schedule, out string? error)
    {
        try
        {
            schedule = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            schedule = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The first matching minute strictly after <paramref name="after"/>, or null if none
    /// within five years (e.g. "0 0 31 2 *").</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after)
    {
        var t = new DateTimeOffset(after.UtcDateTime.Year, after.UtcDateTime.Month, after.UtcDateTime.Day,
            after.UtcDateTime.Hour, after.UtcDateTime.Minute, 0, TimeSpan.Zero).AddMinutes(1);
        var limit = t.AddYears(5);

        while (t < limit)
        {
            if (!_months[t.Month]) { t = new DateTimeOffset(t.Year, t.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1); continue; }
            if (!DayMatches(t)) { t = new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1); continue; }
            if (!_hours[t.Hour]) { t = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero).AddHours(1); continue; }
            if (!_minutes[t.Minute]) { t = t.AddMinutes(1); continue; }
            return t;
        }

        return null;
    }

    private bool DayMatches(DateTimeOffset t)
    {
        var dom = _daysOfMonth[t.Day];
        var dow = _daysOfWeek[(int)t.DayOfWeek];
        if (_domRestricted && _dowRestricted) return dom || dow;
        if (_domRestricted) return dom;
        if (_dowRestricted) return dow;
        return true;
    }

    private static void Fill(bool[] target, string field, int min, int max, string name)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0) throw new FormatException($"Bad step in {name} field '{part}'.");
                range = part[..slash];
            }

            int from, to;
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-');
                if (bounds.Length != 2 || !int.TryParse(bounds[0], out from) || !int.TryParse(bounds[1], out to))
                    throw new FormatException($"Bad range in {name} field '{part}'.");
            }
            else
            {
                if (!int.TryParse(range, out from)) throw new FormatException($"Bad value in {name} field '{part}'.");
                to = slash >= 0 ? max : from;
            }

            if (from < min || to > max || from > to) throw new FormatException($"{name} values must be within {min}-{max} ('{part}').");
            for (var v = from; v <= to; v += step) target[v] = true;
        }
    }
}
