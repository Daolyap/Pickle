using System.Globalization;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Scheduling;

/// <summary>A five-field cron expression (or <c>@daily</c>-style macro): parsing, next run, a plain-English description and conversion from <see cref="TaskTriggerSpec"/>.</summary>
public sealed class CronExpression
{
    private static readonly Dictionary<string, string> Macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
        ["@monthly"] = "0 0 1 * *",
        ["@weekly"] = "0 0 * * 0",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@hourly"] = "0 * * * *",
    };

    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _days;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _weekdays;
    private readonly bool _daysWild;
    private readonly bool _weekdaysWild;

    private CronExpression(string text, bool reboot, HashSet<int> minutes, HashSet<int> hours, HashSet<int> days, HashSet<int> months, HashSet<int> weekdays, bool daysWild, bool weekdaysWild)
    {
        Text = text;
        IsReboot = reboot;
        (_minutes, _hours, _days, _months, _weekdays, _daysWild, _weekdaysWild) = (minutes, hours, days, months, weekdays, daysWild, weekdaysWild);
    }

    public string Text { get; }

    public bool IsReboot { get; }

    public static bool TryParse(string text, out CronExpression expression)
    {
        expression = null!;
        text = text.Trim();
        if (text.Equals("@reboot", StringComparison.OrdinalIgnoreCase))
        {
            expression = new CronExpression("@reboot", true, [], [], [], [], [], false, false);
            return true;
        }

        var normalized = Macros.TryGetValue(text, out var macro) ? macro : text;
        var fields = normalized.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return false;
        }

        if (!Field(fields[0], 0, 59, null, out var minutes) || !Field(fields[1], 0, 23, null, out var hours) || !Field(fields[2], 1, 31, null, out var days)
            || !Field(fields[3], 1, 12, Months, out var months) || !Field(fields[4], 0, 7, Weekdays, out var weekdays))
        {
            return false;
        }

        if (weekdays.Remove(7))
        {
            weekdays.Add(0);
        }

        expression = new CronExpression(text, false, minutes, hours, days, months, weekdays, fields[2].StartsWith('*'), fields[4].StartsWith('*'));
        return true;
    }

    /// <summary>The first matching minute strictly after <paramref name="after"/> within five years, or null (e.g. for @reboot).</summary>
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        if (IsReboot)
        {
            return null;
        }

        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = t.AddYears(5);
        while (t < limit)
        {
            if (!_months.Contains(t.Month))
            {
                t = new DateTime(t.Year, t.Month, 1).AddMonths(1);
                continue;
            }

            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }

            if (!_hours.Contains(t.Hour))
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1);
                continue;
            }

            if (!_minutes.Contains(t.Minute))
            {
                t = t.AddMinutes(1);
                continue;
            }

            return new DateTimeOffset(t, after.Offset);
        }

        return null;
    }

    public string Describe()
    {
        if (IsReboot)
        {
            return "at startup";
        }

        var (minutes, hours) = (_minutes.Order().ToList(), _hours.Order().ToList());
        var everyDay = _daysWild && _weekdaysWild && _months.Count == 12;
        if (everyDay && hours.Count == 24 && minutes.Count == 1 && minutes[0] == 0)
        {
            return "hourly";
        }

        if (everyDay && hours.Count == 24 && minutes.Count == 60)
        {
            return "every minute";
        }

        if (everyDay && hours.Count == 24 && Step(minutes, 60) is { } minuteStep)
        {
            return $"every {minuteStep} min";
        }

        if (everyDay && minutes.Count == 1 && Step(hours, 24) is { } hourStep && hours.Count > 1)
        {
            return $"every {hourStep} h";
        }

        if (minutes.Count == 1 && hours.Count == 1)
        {
            var time = $"{hours[0]:00}:{minutes[0]:00}";
            if (everyDay)
            {
                return "daily at " + time;
            }

            if (_daysWild && _months.Count == 12 && !_weekdaysWild)
            {
                return "weekly on " + string.Join(',', _weekdays.Order().Select(d => ((DayOfWeek)d).ToString()[..3])) + " at " + time;
            }

            if (_weekdaysWild && _months.Count == 12 && !_daysWild)
            {
                return "monthly on day " + string.Join(',', _days.Order()) + " at " + time;
            }
        }

        return Text;
    }

    /// <summary>Cron for a trigger; throws <see cref="NotSupportedException"/> for what cron cannot express (logon, idle, a single run).</summary>
    public static string FromTrigger(TaskTriggerSpec trigger)
    {
        var start = trigger.Start;
        string Clock() => start is { } s ? $"{s.Minute} {s.Hour}" : "0 9";

        switch (trigger.Kind)
        {
            case TaskTriggerKind.AtStartup:
                return "@reboot";
            case TaskTriggerKind.Interval when trigger.RepeatEvery is { } every:
                if (every.TotalMinutes < 60 && 60 % (int)every.TotalMinutes == 0 && every.TotalMinutes == Math.Floor(every.TotalMinutes))
                {
                    return $"*/{(int)every.TotalMinutes} * * * *";
                }

                if (every.TotalHours is >= 1 and < 24 && every.TotalHours == Math.Floor(every.TotalHours) && 24 % (int)every.TotalHours == 0)
                {
                    return $"{start?.Minute ?? 0} */{(int)every.TotalHours} * * *";
                }

                throw new NotSupportedException("Cron repeats every 1, 2, 3, 4, 5, 6, 10, 12, 15, 20 or 30 minutes, or every 1, 2, 3, 4, 6, 8 or 12 hours. Pick one of those, or use a systemd timer.");
            case TaskTriggerKind.Daily when trigger.DaysInterval == 1:
                return $"{Clock()} * * *";
            case TaskTriggerKind.Daily:
                throw new NotSupportedException("Cron can't run every N days; use daily, or a systemd timer.");
            case TaskTriggerKind.Weekly when trigger.WeeksInterval == 1 && trigger.DaysOfWeek is { Count: > 0 } days:
                return $"{Clock()} * * {string.Join(',', days.Select(d => (int)d).Order())}";
            case TaskTriggerKind.Monthly when trigger.DaysOfMonth is { Count: > 0 } monthDays:
                return $"{Clock()} {string.Join(',', monthDays.Order())} * *";
            default:
                throw new NotSupportedException($"Cron can't express '{trigger.Kind}' triggers. Use a systemd timer for logon, idle or one-off schedules.");
        }
    }

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] Weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private bool DayMatches(DateTime t)
    {
        var dayOk = _days.Contains(t.Day);
        var weekdayOk = _weekdays.Contains((int)t.DayOfWeek);
        return _daysWild || _weekdaysWild ? dayOk && weekdayOk : dayOk || weekdayOk;
    }

    private static int? Step(List<int> values, int modulo)
    {
        if (values.Count < 2)
        {
            return null;
        }

        var step = values[1] - values[0];
        return step > 0 && modulo % step == 0 && values.Count == modulo / step && values[0] == 0 && values.Zip(values.Skip(1), (a, b) => b - a).All(d => d == step) ? step : null;
    }

    private static bool Field(string text, int min, int max, string[]? names, out HashSet<int> values)
    {
        values = [];
        foreach (var part in text.Split(','))
        {
            var stepSplit = part.Split('/');
            if (stepSplit.Length > 2)
            {
                return false;
            }

            var step = 1;
            if (stepSplit.Length == 2 && (!int.TryParse(stepSplit[1], CultureInfo.InvariantCulture, out step) || step < 1))
            {
                return false;
            }

            int from, to;
            var range = stepSplit[0];
            if (range == "*")
            {
                (from, to) = (min, max == 7 ? 6 : max);
            }
            else
            {
                var bounds = range.Split('-');
                if (bounds.Length > 2 || !Value(bounds[0], names, min, out from) || !Value(bounds[^1], names, min, out to))
                {
                    return false;
                }

                if (stepSplit.Length == 2 && bounds.Length == 1)
                {
                    to = max == 7 ? 6 : max;
                }
            }

            if (from < min || to > max || from > to)
            {
                return false;
            }

            for (var v = from; v <= to; v += step)
            {
                values.Add(v);
            }
        }

        return values.Count > 0;
    }

    private static bool Value(string text, string[]? names, int offset, out int value)
    {
        if (int.TryParse(text, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        var index = names is null ? -1 : Array.FindIndex(names, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
        value = index + (names == Months ? 1 : 0);
        return index >= 0;
    }
}
