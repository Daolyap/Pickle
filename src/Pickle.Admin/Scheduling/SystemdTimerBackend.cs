using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;

namespace Pickle.Admin.Scheduling;

/// <summary>
/// systemd timers. Creating works for the user's own timers (<c>~/.config/systemd/user/pickle-*.timer</c>, no privileges);
/// system timers can be listed, run, enabled and disabled (through <see cref="IPrivilegeService"/>).
/// </summary>
internal sealed partial class SystemdTimerBackend(IProgramRunner runner, IPrivilegeService privilege, string? userUnitDirectory = null) : IJobBackend
{
    public const string Prefix = "pickle-";
    private const string Properties = "Id,Description,ActiveState,UnitFileState,NextElapseUSecRealtime,LastTriggerUSec,Unit,TimersCalendar,TimersMonotonic";
    private readonly string _userDirectory = userUnitDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");

    public JobKind Kind => JobKind.SystemdTimer;

    public bool IsAvailable => OperatingSystem.IsLinux() && runner.Find("systemctl") is not null;

    public bool CanCreate => IsAvailable;

    public async Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken cancellationToken)
    {
        var jobs = new List<ScheduledJob>();
        foreach (var user in new[] { false, true })
        {
            var scope = user ? new[] { "--user" } : [];
            var files = await runner.RunAsync("systemctl", [.. scope, "list-unit-files", "--type=timer", "--no-legend", "--no-pager", "--plain"], null, cancellationToken).ConfigureAwait(false);
            var names = files.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0])
                .Where(n => n.EndsWith(".timer", StringComparison.Ordinal) && IsValidUnit(n))
                .ToList();
            if (names.Count == 0)
            {
                continue;
            }

            var shown = await runner.RunAsync("systemctl", [.. scope, "show", "--no-pager", "-p", Properties, "--", .. names.Take(300)], null, cancellationToken).ConfigureAwait(false);
            jobs.AddRange(ParseShow(shown.StdOut, user));
        }

        return [.. jobs.OrderBy(j => j.Scope).ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<ServiceOperationResult> CreateAsync(NewJob job, CancellationToken cancellationToken)
    {
        var name = Prefix + UnitName(job.Name);
        var (timerLines, description) = TimerSection(job.Trigger);
        if (job.Command.Any(c => c is '\n' or '\r' or '\0') || string.IsNullOrWhiteSpace(job.Command))
        {
            throw new ArgumentException("The command must be one line.");
        }

        var sh = runner.Find("sh") ?? "/bin/sh";
        var text = job.Description ?? job.Name;
        var service = $"[Unit]\nDescription={Clean(text)} (created by Pickle)\n\n[Service]\nType=oneshot\nExecStart={sh} -c {Quote(job.Command)}\n";
        var timer = $"[Unit]\nDescription={Clean(text)} ({description}, created by Pickle)\n\n[Timer]\n{string.Join('\n', timerLines)}\n\n[Install]\nWantedBy=timers.target\n";
        Directory.CreateDirectory(_userDirectory);
        var timerPath = Path.Combine(_userDirectory, name + ".timer");
        if (File.Exists(timerPath))
        {
            return new ServiceOperationResult(false, $"A timer called {name} already exists.");
        }

        await File.WriteAllTextAsync(Path.Combine(_userDirectory, name + ".service"), service, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(timerPath, timer, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var reload = await runner.RunAsync("systemctl", ["--user", "daemon-reload"], null, cancellationToken).ConfigureAwait(false);
        var enable = await runner.RunAsync("systemctl", ["--user", "enable", "--now", "--", name + ".timer"], null, cancellationToken).ConfigureAwait(false);
        return reload.Success && enable.Success
            ? new ServiceOperationResult(true, $"Created {name}.timer ({description}). It runs while you are logged in; 'loginctl enable-linger' keeps it running when you are not.")
            : new ServiceOperationResult(false, enable.Success ? reload.Message : enable.Message);
    }

    public Task<ServiceOperationResult> RunNowAsync(ScheduledJob job, CancellationToken cancellationToken) =>
        ControlAsync(job, ["start", "--", ServiceOf(job)], $"{job.Name} started.", cancellationToken);

    public Task<ServiceOperationResult> SetEnabledAsync(ScheduledJob job, bool enabled, CancellationToken cancellationToken) =>
        ControlAsync(job, [enabled ? "enable" : "disable", "--now", "--", UnitOf(job)], $"{job.Name} is {(enabled ? "on" : "off")}.", cancellationToken);

    public async Task<ServiceOperationResult> DeleteAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        if (!job.Managed || job.Scope != "user")
        {
            return new ServiceOperationResult(false, "Only timers Pickle created can be deleted here. Disable others instead.");
        }

        var unit = UnitOf(job);
        await runner.RunAsync("systemctl", ["--user", "disable", "--now", "--", unit], null, cancellationToken).ConfigureAwait(false);
        foreach (var file in new[] { unit, ServiceOf(job) })
        {
            var path = Path.Combine(_userDirectory, file);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        await runner.RunAsync("systemctl", ["--user", "daemon-reload"], null, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(true, $"{job.Name} deleted.");
    }

    public string? HistoryCommand(ScheduledJob job) =>
        IsValidUnit(job.Id) ? PowerShellQuote.Command("journalctl", [.. (job.Scope == "user" ? new[] { "--user" } : []), "--no-pager", "-n", "50", "-u", ServiceOf(job)]) : null;

    public static IReadOnlyList<ScheduledJob> ParseShow(string output, bool user)
    {
        var jobs = new List<ScheduledJob>();
        foreach (var block in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var properties = block.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0])
                .ToDictionary(g => g.Key, g => g.Last()[1]);
            if (!properties.TryGetValue("Id", out var id) || !id.EndsWith(".timer", StringComparison.Ordinal))
            {
                continue;
            }

            var schedule = string.Join("; ", new[] { properties.GetValueOrDefault("TimersCalendar"), properties.GetValueOrDefault("TimersMonotonic") }
                .Where(s => !string.IsNullOrEmpty(s))
                .SelectMany(s => ScheduleParts().Matches(s!).Select(m => m.Groups[1].Value + "=" + m.Groups[2].Value.Trim())));
            var enabled = properties.GetValueOrDefault("ActiveState") == "active" || properties.GetValueOrDefault("UnitFileState") is "enabled" or "enabled-runtime" or "static";
            var service = properties.GetValueOrDefault("Unit") ?? id[..^6] + ".service";
            jobs.Add(new ScheduledJob(id, id[..^6], JobKind.SystemdTimer, schedule.Length > 0 ? schedule : "(no schedule)", service, enabled)
            {
                NextRun = ParseTimestamp(properties.GetValueOrDefault("NextElapseUSecRealtime")),
                LastRun = ParseTimestamp(properties.GetValueOrDefault("LastTriggerUSec")),
                Scope = user ? "user" : "system",
                Managed = user && id.StartsWith(Prefix, StringComparison.Ordinal),
                Description = properties.GetValueOrDefault("Description"),
            });
        }

        return jobs;
    }

    // "Wed 2025-10-01 06:00:00 UTC" or "n/a" or empty
    public static DateTimeOffset? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "n/a")
        {
            return null;
        }

        var match = Timestamp().Match(text);
        if (!match.Success || !DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }

        var utc = match.Groups[2].Value is "UTC" or "GMT" or "Z";
        return utc ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time));
    }

    /// <summary>The <c>[Timer]</c> lines and a short description for a trigger; throws <see cref="NotSupportedException"/> for what a timer cannot express.</summary>
    public static (IReadOnlyList<string> Lines, string Description) TimerSection(TaskTriggerSpec trigger)
    {
        string Clock() => trigger.Start is { } s ? $"{s.Hour:00}:{s.Minute:00}:00" : "09:00:00";
        switch (trigger.Kind)
        {
            case TaskTriggerKind.Daily when trigger.DaysInterval == 1:
                return ([$"OnCalendar=*-*-* {Clock()}", "Persistent=true"], "daily at " + Clock()[..5]);
            case TaskTriggerKind.Weekly when trigger.WeeksInterval == 1 && trigger.DaysOfWeek is { Count: > 0 } days:
                var names = string.Join(',', days.Select(d => d.ToString()[..3]));
                return ([$"OnCalendar={names} *-*-* {Clock()}", "Persistent=true"], $"{names} at {Clock()[..5]}");
            case TaskTriggerKind.Monthly when trigger.DaysOfMonth is { Count: > 0 } monthDays:
                return ([$"OnCalendar=*-*-{string.Join(',', monthDays.Order())} {Clock()}", "Persistent=true"], $"monthly on {string.Join(',', monthDays.Order())}");
            case TaskTriggerKind.Once when trigger.Start is { } at:
                return ([$"OnCalendar={at.LocalDateTime:yyyy-MM-dd HH:mm:00}", "RemainAfterElapse=no"], "once");
            case TaskTriggerKind.Interval when trigger.RepeatEvery is { } every && every >= TimeSpan.FromMinutes(1):
                var seconds = (long)every.TotalSeconds;
                return (["OnStartupSec=1min", $"OnUnitActiveSec={seconds}s"], $"every {seconds}s");
            case TaskTriggerKind.AtStartup or TaskTriggerKind.AtLogon:
                return (["OnStartupSec=1min"], trigger.Kind == TaskTriggerKind.AtLogon ? "at login" : "at startup");
            default:
                throw new NotSupportedException($"A systemd timer can't express a '{trigger.Kind}' trigger{(trigger.Kind == TaskTriggerKind.Daily ? " with a day interval" : string.Empty)}.");
        }
    }

    /// <summary>The argument for <c>ExecStart=/bin/sh -c …</c>: double quotes, with systemd's own escapes (\\, \", %% and $$).</summary>
    public static string Quote(string command) =>
        "\"" + command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal) + "\"";

    public static bool IsValidUnit(string id) => UnitPattern().IsMatch(id);

    private async Task<ServiceOperationResult> ControlAsync(ScheduledJob job, string[] arguments, string message, CancellationToken cancellationToken)
    {
        if (!IsValidUnit(job.Id))
        {
            throw new ArgumentException($"'{job.Id}' is not a timer name.");
        }

        if (job.Scope == "user")
        {
            var result = await runner.RunAsync("systemctl", ["--user", .. arguments], null, cancellationToken).ConfigureAwait(false);
            return new ServiceOperationResult(result.Success, result.Success ? message : result.Message);
        }

        var command = new PrivilegedCommand("systemctl", arguments, message);
        var outcome = await privilege.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return new ServiceOperationResult(outcome.Success, outcome.Success ? message : outcome.Message) { ShellCommand = outcome.NeedsTerminal ? privilege.ShellCommand(command) : null };
    }

    private static string UnitOf(ScheduledJob job) => job.Id;

    private static string ServiceOf(ScheduledJob job) => job.Command.EndsWith(".service", StringComparison.Ordinal) && IsValidUnit(job.Command) ? job.Command : job.Id[..^6] + ".service";

    private static string UnitName(string name)
    {
        var clean = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        return clean.Length is > 0 and <= 40 ? clean : throw new ArgumentException("Give the timer a short name (letters, digits, - and _).");
    }

    private static string Clean(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Trim();

    [GeneratedRegex(@"(On[A-Za-z]+)=([^;}]+)")]
    private static partial Regex ScheduleParts();

    [GeneratedRegex(@"^(?:[A-Za-z]{3}\s+)?(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\s*([A-Za-z]+)?")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:_.@\\-]{0,200}\.(timer|service)$")]
    private static partial Regex UnitPattern();
}
