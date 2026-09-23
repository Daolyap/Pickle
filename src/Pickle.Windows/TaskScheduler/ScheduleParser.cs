using System.Globalization;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;

namespace Pickle.Windows.TaskScheduler;

/// <summary>
/// Friendly schedule syntax → <see cref="TaskTriggerSpec"/>. Pure (clock and time zone are parameters) so it is
/// tested on every OS. Times are 24-hour local times in <c>zone</c>.
/// </summary>
public static partial class ScheduleParser
{
    public const string Examples =
        "every 30m | every 2h | every 1d | daily 09:00 | weekly mon,fri 18:30 | monthly 1,15 08:00 | " +
        "at logon | at startup | on idle | once 2026-10-01 12:00 | in 10m";

    private static readonly TimeSpan MaxRepetition = TimeSpan.FromDays(31);

    [GeneratedRegex(@"^every\s+(?<n>\d{1,6})\s*(?<unit>[a-z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EveryRegex();

    [GeneratedRegex(@"^in\s+(?<n>\d{1,6})\s*(?<unit>[a-z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex InRegex();

    [GeneratedRegex(@"^daily(?:\s+(?<time>\S+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyRegex();

    [GeneratedRegex(@"^weekly\s+(?<days>[a-z,\-\s]+?)(?:\s+(?<time>\d\S*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex WeeklyRegex();

    [GeneratedRegex(@"^monthly\s+(?<days>[0-9,\s]+?)(?:\s+(?<time>\d{1,2}:\S*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthlyRegex();

    [GeneratedRegex(@"^once\s+(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OnceRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    public static TaskTriggerSpec Parse(string text) => Parse(text, DateTimeOffset.Now, TimeZoneInfo.Local);

    public static TaskTriggerSpec Parse(string text, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("The schedule is empty. Examples: " + Examples);
        }

        var s = Spaces().Replace(text.Trim().ToLowerInvariant(), " ");
        var local = TimeZoneInfo.ConvertTime(now, zone);

        switch (s)
        {
            case "at logon" or "at login" or "on logon":
                return new TaskTriggerSpec(TaskTriggerKind.AtLogon);
            case "at startup" or "at boot" or "on startup" or "on boot":
                return new TaskTriggerSpec(TaskTriggerKind.AtStartup);
            case "on idle" or "when idle" or "at idle":
                return new TaskTriggerSpec(TaskTriggerKind.OnIdle);
        }

        if (EveryRegex().Match(s) is { Success: true } every)
        {
            var interval = ParseDuration(every.Groups["n"].Value, every.Groups["unit"].Value, s);
            if (interval.TotalDays >= 1 && interval.TotalDays == Math.Floor(interval.TotalDays))
            {
                var days = (int)interval.TotalDays;
                if (days > 365)
                {
                    throw new FormatException($"'{text}': the day interval must be between 1 and 365.");
                }

                return new TaskTriggerSpec(TaskTriggerKind.Daily, At(local.Date + local.TimeOfDay.Truncate(), zone), DaysInterval: days);
            }

            if (interval < TimeSpan.FromMinutes(1) || interval > MaxRepetition)
            {
                throw new FormatException($"'{text}': the interval must be between 1 minute and 31 days.");
            }

            return new TaskTriggerSpec(TaskTriggerKind.Interval, At(local.Date + local.TimeOfDay.Truncate(), zone), RepeatEvery: interval);
        }

        if (InRegex().Match(s) is { Success: true } inMatch)
        {
            var delay = ParseDuration(inMatch.Groups["n"].Value, inMatch.Groups["unit"].Value, s);
            if (delay < TimeSpan.FromMinutes(1) || delay > TimeSpan.FromDays(366))
            {
                throw new FormatException($"'{text}': the delay must be between 1 minute and 366 days.");
            }

            return new TaskTriggerSpec(TaskTriggerKind.Once, (now + delay).ToOffset(zone.GetUtcOffset(now + delay)));
        }

        if (DailyRegex().Match(s) is { Success: true } daily)
        {
            var time = ParseTime(daily.Groups["time"].Success ? daily.Groups["time"].Value : null, text);
            return new TaskTriggerSpec(TaskTriggerKind.Daily, NextOccurrence(local, time, zone));
        }

        if (WeeklyRegex().Match(s) is { Success: true } weekly)
        {
            var days = ParseDaysOfWeek(weekly.Groups["days"].Value, text);
            var time = ParseTime(weekly.Groups["time"].Success ? weekly.Groups["time"].Value : null, text);
            var start = local.Date + time;
            for (var i = 0; i < 8 && (!days.Contains(start.DayOfWeek) || start <= local.DateTime); i++)
            {
                start = start.AddDays(1);
            }

            return new TaskTriggerSpec(TaskTriggerKind.Weekly, At(start, zone), DaysOfWeek: days);
        }

        if (MonthlyRegex().Match(s) is { Success: true } monthly)
        {
            var days = ParseDaysOfMonth(monthly.Groups["days"].Value, text);
            var time = ParseTime(monthly.Groups["time"].Success ? monthly.Groups["time"].Value : null, text);
            return new TaskTriggerSpec(TaskTriggerKind.Monthly, NextOccurrence(local, time, zone), DaysOfMonth: days);
        }

        if (OnceRegex().Match(s) is { Success: true } once)
        {
            var rest = once.Groups["rest"].Value;
            DateTime when;
            if (DateTime.TryParseExact(rest, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                when = parsed;
            }
            else if (TryParseTime(rest, out var timeOnly))
            {
                when = NextOccurrence(local, timeOnly, zone).DateTime;
            }
            else
            {
                throw new FormatException($"'{text}': use 'once yyyy-MM-dd HH:mm' (24-hour), e.g. 'once 2026-10-01 12:00'.");
            }

            if (when <= local.DateTime)
            {
                throw new FormatException($"'{text}': {when:yyyy-MM-dd HH:mm} is in the past.");
            }

            return new TaskTriggerSpec(TaskTriggerKind.Once, At(when, zone));
        }

        throw new FormatException($"Unrecognized schedule '{text}'. Examples: {Examples}");
    }

    /// <summary>A short human description of a trigger spec (used by panels and pk output).</summary>
    public static string Describe(TaskTriggerSpec spec)
    {
        var time = spec.Start is { } start ? start.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        return spec.Kind switch
        {
            TaskTriggerKind.Once => spec.Start is { } s ? $"once at {s:yyyy-MM-dd HH:mm}" : "once",
            TaskTriggerKind.Daily => spec.DaysInterval > 1 ? $"every {spec.DaysInterval} days at {time}" : $"daily at {time}",
            TaskTriggerKind.Weekly => $"weekly on {string.Join(",", (spec.DaysOfWeek ?? []).Select(d => d.ToString()[..3]))} at {time}",
            TaskTriggerKind.Monthly => $"monthly on day {string.Join(",", spec.DaysOfMonth ?? [])} at {time}",
            TaskTriggerKind.AtLogon => "at logon",
            TaskTriggerKind.AtStartup => "at startup",
            TaskTriggerKind.OnIdle => "when idle",
            TaskTriggerKind.Interval => $"every {FormatInterval(spec.RepeatEvery ?? TimeSpan.Zero)}",
            _ => spec.Kind.ToString(),
        };
    }

    private static string FormatInterval(TimeSpan interval) =>
        interval.TotalHours >= 1 && interval.TotalHours == Math.Floor(interval.TotalHours)
            ? $"{interval.TotalHours:0}h"
            : $"{interval.TotalMinutes:0}m";

    private static TimeSpan ParseDuration(string number, string unit, string text)
    {
        var n = int.Parse(number, CultureInfo.InvariantCulture);
        if (n <= 0)
        {
            throw new FormatException($"'{text}': the number must be positive.");
        }

        return unit switch
        {
            "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(n),
            "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(n),
            "d" or "day" or "days" => TimeSpan.FromDays(n),
            _ => throw new FormatException($"'{text}': unknown unit '{unit}' (use m, h or d)."),
        };
    }

    private static TimeSpan ParseTime(string? value, string text)
    {
        if (value is null)
        {
            throw new FormatException($"'{text}': add a time, e.g. 09:00 (24-hour).");
        }

        return TryParseTime(value, out var time)
            ? time
            : throw new FormatException($"'{text}': invalid time '{value}' (use HH:mm, 24-hour).");
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        time = default;
        var parts = value.Split(':');
        if (parts.Length != 2 || parts[1].Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || h > 23 || m > 59 || parts[0].Length is 0 or > 2)
        {
            return false;
        }

        time = new TimeSpan(h, m, 0);
        return true;
    }

    private static List<DayOfWeek> ParseDaysOfWeek(string value, string text)
    {
        var days = new SortedSet<DayOfWeek>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw)
            {
                case "weekdays":
                    days.UnionWith([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
                    continue;
                case "weekends" or "weekend":
                    days.UnionWith([DayOfWeek.Saturday, DayOfWeek.Sunday]);
                    continue;
            }

            var range = raw.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 2)
            {
                var from = ParseDay(range[0], text);
                var to = ParseDay(range[1], text);
                for (var d = from; ; d = (DayOfWeek)(((int)d + 1) % 7))
                {
                    days.Add(d);
                    if (d == to)
                    {
                        break;
                    }
                }
            }
            else
            {
                days.Add(ParseDay(raw, text));
            }
        }

        return days.Count > 0 ? [.. days] : throw new FormatException($"'{text}': list the days, e.g. 'weekly mon,fri 18:30'.");
    }

    private static DayOfWeek ParseDay(string value, string text) => value switch
    {
        "sun" or "sunday" => DayOfWeek.Sunday,
        "mon" or "monday" => DayOfWeek.Monday,
        "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
        "wed" or "wednesday" => DayOfWeek.Wednesday,
        "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
        "fri" or "friday" => DayOfWeek.Friday,
        "sat" or "saturday" => DayOfWeek.Saturday,
        _ => throw new FormatException($"'{text}': unknown day '{value}' (use mon,tue,wed,thu,fri,sat,sun)."),
    };

    private static List<int> ParseDaysOfMonth(string value, string text)
    {
        var days = new SortedSet<int>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day is < 1 or > 31)
            {
                throw new FormatException($"'{text}': day of month must be 1-31, got '{raw}'.");
            }

            days.Add(day);
        }

        return days.Count > 0 ? [.. days] : throw new FormatException($"'{text}': list the days of the month, e.g. 'monthly 1,15 08:00'.");
    }

    private static DateTimeOffset NextOccurrence(DateTimeOffset local, TimeSpan time, TimeZoneInfo zone)
    {
        var candidate = local.Date + time;
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return At(candidate, zone);
    }

    private static DateTimeOffset At(DateTime localClock, TimeZoneInfo zone)
    {
        var clock = DateTime.SpecifyKind(localClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(clock, zone.GetUtcOffset(clock));
    }

    private static TimeSpan Truncate(this TimeSpan value) => new(value.Hours, value.Minutes, 0);
}
