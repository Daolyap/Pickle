using Pickle.Abstractions.Services;
using Pickle.Admin.Privilege;
using Pickle.Admin.Scheduling;
using Pickle.Testing.Fakes;

namespace Pickle.Admin.Tests.Scheduling;

public sealed class SchedulingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2025, 10, 1, 12, 34, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("pickle-sched").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TaskTriggerSpec Trigger(string text) => ScheduleParser.Parse(text, new DateTimeOffset(2025, 10, 1, 8, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    [Theory]
    [InlineData("*/15 * * * *", "2025-10-01T12:45:00", "every 15 min")]
    [InlineData("0 9 * * *", "2025-10-02T09:00:00", "daily at 09:00")]
    [InlineData("30 18 * * 1,5", "2025-10-03T18:30:00", "weekly on Mon,Fri at 18:30")]
    [InlineData("0 8 1,15 * *", "2025-10-15T08:00:00", "monthly on day 1,15 at 08:00")]
    [InlineData("0 */2 * * *", "2025-10-01T14:00:00", "every 2 h")]
    [InlineData("@hourly", "2025-10-01T13:00:00", "hourly")]
    [InlineData("0 0 * * Sun", "2025-10-05T00:00:00", "weekly on Sun at 00:00")]
    public void CronNextRunAndDescription(string text, string next, string description)
    {
        Assert.True(CronExpression.TryParse(text, out var cron));

        Assert.Equal(DateTimeOffset.Parse(next + "+00:00"), cron.Next(Now));
        Assert.Equal(description, cron.Describe());
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("a b c d e")]
    public void BadCronIsRejected(string text) => Assert.False(CronExpression.TryParse(text, out _));

    [Fact]
    public void RebootHasNoNextRun()
    {
        Assert.True(CronExpression.TryParse("@reboot", out var cron));
        Assert.Null(cron.Next(Now));
        Assert.Equal("at startup", cron.Describe());
    }

    [Theory]
    [InlineData("every 30m", "*/30 * * * *")]
    [InlineData("every 2h", "0 */2 * * *")]
    [InlineData("daily 09:15", "15 9 * * *")]
    [InlineData("weekly mon,fri 18:30", "30 18 * * 1,5")]
    [InlineData("monthly 1,15 08:00", "0 8 1,15 * *")]
    [InlineData("at startup", "@reboot")]
    public void FriendlySchedulesBecomeCron(string text, string cron) => Assert.Equal(cron, CronExpression.FromTrigger(Trigger(text)));

    [Theory]
    [InlineData("every 7m")]
    [InlineData("at logon")]
    [InlineData("on idle")]
    [InlineData("once 2026-10-01 12:00")]
    [InlineData("every 2d")]
    public void WhatCronCannotDoSaysSo(string text) => Assert.Throws<NotSupportedException>(() => CronExpression.FromTrigger(Trigger(text)));

    [Fact]
    public void CrontabIsParsedWithTagsDisabledLinesAndEnvironment()
    {
        const string crontab = "MAILTO=me@example.com\n# nightly\n0 2 * * * /usr/bin/backup --full\n# pickle:morning\n0 9 * * 1-5 pk upgrade --yes\n#pickle-off# */10 * * * * echo 50\\% done\n\n@reboot /usr/local/bin/start\n";

        var document = CronBackend.Parse(crontab, Now);

        Assert.Equal(["line 3: backup", "morning", "line 6: echo", "line 8: start"], document.Jobs.Select(j => j.Name));
        Assert.Equal([false, true, false, false], document.Jobs.Select(j => j.Managed));
        Assert.Equal([true, true, false, true], document.Jobs.Select(j => j.Enabled));
        Assert.Equal("echo 50% done", document.Jobs[2].Command);
        Assert.Null(document.Jobs[2].NextRun);
        Assert.Equal("weekly on Mon,Tue,Wed,Thu,Fri at 09:00", document.Jobs[1].Schedule);
    }

    [Fact]
    public async Task CronCreateAppendsATaggedLineAndWritesTheWholeCrontabThroughStdin()
    {
        string? written = null;
        var runner = new FakeProgramRunner().On("crontab", (args, options) =>
        {
            if (args[0] == "-l")
            {
                return new ProgramResult(0, "MAILTO=me\n0 2 * * * backup\n", string.Empty);
            }

            written = options?.StandardInput;
            return new ProgramResult(0, string.Empty, string.Empty);
        });
        var scheduler = new UnixJobScheduler(runner, new UnixPrivilegeService(runner, () => false, _ => null), () => Now, _dir, _dir);

        var result = await scheduler.CreateAsync(new NewJob("Morning Upgrade", Trigger("daily 09:00"), "pk upgrade --yes && echo 100%"), JobKind.Cron);

        Assert.True(result.Success, result.Message);
        Assert.Equal("MAILTO=me\n0 2 * * * backup\n\n# pickle:morning-upgrade\n0 9 * * * pk upgrade --yes && echo 100\\%\n", written);
    }

    [Fact]
    public async Task CronToggleAndDeleteTouchOnlyTheirOwnLines()
    {
        var crontab = "# pickle:a\n0 9 * * * one\n# pickle:b\n0 10 * * * two\n";
        var writes = new List<string>();
        var runner = new FakeProgramRunner().On("crontab", (args, options) =>
        {
            if (args[0] == "-l")
            {
                return new ProgramResult(0, crontab, string.Empty);
            }

            writes.Add(options!.StandardInput!);
            crontab = options.StandardInput!;
            return new ProgramResult(0, string.Empty, string.Empty);
        });
        var scheduler = new UnixJobScheduler(runner, new UnixPrivilegeService(runner, () => false, _ => null), () => Now, _dir, _dir);

        var jobs = await scheduler.ListAsync();
        await scheduler.SetEnabledAsync(jobs.Single(j => j.Name == "b"), false);
        var afterOff = (await scheduler.ListAsync()).Single(j => j.Name == "b");
        await scheduler.SetEnabledAsync(afterOff, true);
        await scheduler.DeleteAsync((await scheduler.ListAsync()).Single(j => j.Name == "a"));

        Assert.Equal("# pickle:a\n0 9 * * * one\n# pickle:b\n#pickle-off# 0 10 * * * two\n", writes[0]);
        Assert.False(afterOff.Enabled);
        Assert.Equal("# pickle:a\n0 9 * * * one\n# pickle:b\n0 10 * * * two\n", writes[1]);
        Assert.Equal("# pickle:b\n0 10 * * * two\n", writes[2]);
    }

    [Fact]
    public async Task CronRunUsesShellWithTheCommandAsOneArgument()
    {
        var runner = new FakeProgramRunner().On("crontab", "-l", "0 9 * * * echo hi; echo there\n").On("sh", "-c", "hi\nthere\n");
        var scheduler = new UnixJobScheduler(runner, new UnixPrivilegeService(runner, () => false, _ => null), () => Now, _dir, _dir);
        var job = (await scheduler.ListAsync()).Single();

        var result = await scheduler.RunNowAsync(job);

        Assert.True(result.Success);
        Assert.Equal(["-c echo hi; echo there"], runner.CommandLines("sh"));
    }

    [Fact]
    public void SystemdShowOutputBecomesJobsWithSchedulesAndTimes()
    {
        const string show = "Id=apt-daily.timer\nDescription=Daily apt download activities\nActiveState=active\nUnitFileState=enabled\nNextElapseUSecRealtime=Thu 2025-10-02 06:12:00 UTC\nLastTriggerUSec=Wed 2025-10-01 06:30:00 UTC\nUnit=apt-daily.service\nTimersCalendar={ OnCalendar=*-*-* 6,18:00 ; next_elapse=Thu 2025-10-02 06:12:00 UTC }\nTimersMonotonic=\n\nId=pickle-backup.timer\nDescription=Backup\nActiveState=inactive\nUnitFileState=disabled\nNextElapseUSecRealtime=\nLastTriggerUSec=n/a\nUnit=pickle-backup.service\nTimersCalendar=\nTimersMonotonic={ OnUnitActiveSec=30min ; next_elapse=n/a }\n";

        var jobs = SystemdTimerBackend.ParseShow(show, user: true);

        Assert.Equal(["apt-daily", "pickle-backup"], jobs.Select(j => j.Name));
        Assert.Equal("OnCalendar=*-*-* 6,18:00", jobs[0].Schedule);
        Assert.Equal(new DateTimeOffset(2025, 10, 2, 6, 12, 0, TimeSpan.Zero), jobs[0].NextRun);
        Assert.Equal(new DateTimeOffset(2025, 10, 1, 6, 30, 0, TimeSpan.Zero), jobs[0].LastRun);
        Assert.Equal([true, false], jobs.Select(j => j.Enabled));
        Assert.Equal([false, true], jobs.Select(j => j.Managed));
        Assert.Equal("OnUnitActiveSec=30min", jobs[1].Schedule);
        Assert.Null(jobs[1].NextRun);
        Assert.All(jobs, j => Assert.Equal("user", j.Scope));
    }

    [Theory]
    [InlineData("daily 09:15", "OnCalendar=*-*-* 09:15:00")]
    [InlineData("weekly mon,fri 18:30", "OnCalendar=Mon,Fri *-*-* 18:30:00")]
    [InlineData("monthly 1,15 08:00", "OnCalendar=*-*-1,15 08:00:00")]
    [InlineData("every 30m", "OnUnitActiveSec=1800s")]
    [InlineData("at logon", "OnStartupSec=1min")]
    public void TriggersBecomeTimerLines(string text, string expected) => Assert.Contains(expected, SystemdTimerBackend.TimerSection(Trigger(text)).Lines);

    [Fact]
    public void IdleTriggersAreRefused() => Assert.Throws<NotSupportedException>(() => SystemdTimerBackend.TimerSection(Trigger("on idle")));

    [Fact]
    public void ExecStartQuotingEscapesWhatSystemdInterprets() =>
        Assert.Equal("\"echo \\\"a\\\" 100%% $$HOME \\\\n\"", SystemdTimerBackend.Quote("echo \"a\" 100% $HOME \\n"));

    [Fact]
    public async Task CreatingAUserTimerWritesUnitFilesThenReloadsAndEnables()
    {
        var runner = new FakeProgramRunner().On("systemctl", "--user", string.Empty).On("sh", "-c", string.Empty);
        var scheduler = new UnixJobScheduler(runner, new UnixPrivilegeService(runner, () => false, _ => null), () => Now, Path.Combine(_dir, "units"), _dir);

        var result = await scheduler.CreateAsync(new NewJob("Nightly Backup", Trigger("daily 02:30"), "~/bin/backup --all", "Backup my files"), JobKind.SystemdTimer);

        Assert.True(result.Success, result.Message);
        var service = File.ReadAllText(Path.Combine(_dir, "units", "pickle-nightly-backup.service"));
        var timer = File.ReadAllText(Path.Combine(_dir, "units", "pickle-nightly-backup.timer"));
        Assert.Contains("ExecStart=/usr/bin/sh -c \"~/bin/backup --all\"", service, StringComparison.Ordinal);
        Assert.Contains("OnCalendar=*-*-* 02:30:00", timer, StringComparison.Ordinal);
        Assert.Contains("WantedBy=timers.target", timer, StringComparison.Ordinal);
        Assert.Equal(["--user daemon-reload", "--user enable --now -- pickle-nightly-backup.timer"], runner.CommandLines("systemctl"));
    }

    [Fact]
    public async Task OnlyPickleTimersCanBeDeletedAndSystemOnesGoThroughSudo()
    {
        var runner = new FakeProgramRunner().On("systemctl", "--user", string.Empty).On("systemctl", "start", string.Empty).On("sudo", "-n", string.Empty);
        var scheduler = new UnixJobScheduler(runner, new UnixPrivilegeService(runner, () => false, _ => null), () => Now, _dir, _dir);
        var system = new ScheduledJob("fstrim.timer", "fstrim", JobKind.SystemdTimer, "weekly", "fstrim.service", true) { Scope = "system" };
        var foreign = new ScheduledJob("mine.timer", "mine", JobKind.SystemdTimer, "daily", "mine.service", true) { Scope = "user" };

        var deleted = await scheduler.DeleteAsync(foreign);
        var started = await scheduler.RunNowAsync(system);

        Assert.False(deleted.Success);
        Assert.True(started.Success);
        Assert.Equal(["-n /usr/bin/systemctl start -- fstrim.service"], runner.CommandLines("sudo"));
        Assert.Equal("journalctl --no-pager -n 50 -u fstrim.service", scheduler.HistoryCommand(system));
    }

    [Fact]
    public void LaunchdPlistsRoundTrip()
    {
        var plist = LaunchdBackend.BuildPlist("com.pickle.backup", "~/bin/backup \"all\" & done", Trigger("weekly mon 07:30"));

        var job = LaunchdBackend.ParsePlist(plist)!;

        Assert.Equal(("com.pickle.backup", "backup", "calendar", true), (job.Id, job.Name, job.Schedule, job.Managed));
        Assert.Equal("~/bin/backup \"all\" & done", job.Command);
        Assert.Contains("<key>Weekday</key>", plist, StringComparison.Ordinal);
        Assert.Equal("every 1800s", LaunchdBackend.ParsePlist(LaunchdBackend.BuildPlist("com.pickle.x", "x", Trigger("every 30m")))!.Schedule);
        Assert.Throws<NotSupportedException>(() => LaunchdBackend.BuildPlist("com.pickle.x", "x", Trigger("on idle")));
        Assert.Null(LaunchdBackend.ParsePlist("not xml"));
    }
}
