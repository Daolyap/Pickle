using Pickle.Abstractions.Services;
using Pickle.Admin.Logs;
using Pickle.Testing.Fakes;

namespace Pickle.Admin.Tests.Logs;

public class LogSourceTests
{
    // Real journalctl -o json lines (shortened). 1727784000000000 µs = 2024-10-01 12:00:00 UTC.
    private const string Journal = """
        {"__REALTIME_TIMESTAMP":"1727784000000000","PRIORITY":"3","SYSLOG_IDENTIFIER":"sshd","_SYSTEMD_UNIT":"ssh.service","_PID":"812","_HOSTNAME":"box","MESSAGE":"Failed password for root from 10.0.0.9"}
        {"__REALTIME_TIMESTAMP":"1727784060000000","PRIORITY":"6","SYSLOG_IDENTIFIER":"systemd","MESSAGE":"Started Daily apt download activities."}
        {"__REALTIME_TIMESTAMP":"1727783940000000","PRIORITY":"4","_COMM":"kernel","MESSAGE":[72,105,32,226,156,147]}
        not json at all
        """;

    [Fact]
    public void ParsesJournalEntriesNewestFirstWithSeverityAndFields()
    {
        var entries = JournaldLogSource.Parse(Journal);

        Assert.Equal(["Started Daily apt download activities.", "Failed password for root from 10.0.0.9", "Hi ✓"], entries.Select(e => e.Message));
        Assert.Equal([LogSeverity.Info, LogSeverity.Error, LogSeverity.Warning], entries.Select(e => e.Severity));
        var ssh = entries[1];
        Assert.Equal("sshd", ssh.Source);
        Assert.Equal("ssh.service", ssh.Fields["Unit"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1727784000), ssh.Time);
    }

    [Fact]
    public async Task QueryPassesFiltersAsSeparateArguments()
    {
        var runner = new FakeProgramRunner().On("journalctl", "--no-pager", Journal);
        var source = new JournaldLogSource(runner);

        var entries = await source.QueryAsync(new LogQuery { Max = 50, MinSeverity = LogSeverity.Warning, Source = "kernel", Unit = "ssh.service", Text = "password", Since = TimeSpan.FromHours(2) });

        Assert.Single(entries);
        var args = runner.Calls.Single().Arguments;
        Assert.Contains("-p", args);
        Assert.Equal("4", args[args.ToList().IndexOf("-p") + 1]);
        Assert.Contains("-k", args);
        Assert.Equal("ssh.service", args[args.ToList().IndexOf("-u") + 1]);
        Assert.Equal("50", args[args.ToList().IndexOf("-n") + 1]);
    }

    [Theory]
    [InlineData("ssh; rm -rf /")]
    [InlineData("a b")]
    [InlineData("$(x)")]
    public async Task UnitNamesAreChecked(string unit)
    {
        var source = new JournaldLogSource(new FakeProgramRunner().On("journalctl", "--no-pager", string.Empty));

        await Assert.ThrowsAsync<ArgumentException>(() => source.QueryAsync(new LogQuery { Unit = unit }));
    }

    [Fact]
    public void FollowCommandIsAShellLineThatKeepsNoUntrustedText()
    {
        var source = new JournaldLogSource(new FakeProgramRunner());

        Assert.Equal("journalctl --no-pager -o short-iso -n 300 -f -p 3 -u ssh.service", source.FollowCommand(new LogQuery { MinSeverity = LogSeverity.Error, Unit = "ssh.service" }));
    }

    [Fact]
    public void ParsesClassicAndIsoSyslogLines()
    {
        var now = new DateTimeOffset(2025, 10, 1, 12, 30, 0, TimeSpan.Zero);

        var classic = SyslogFileLogSource.Parse("Oct  1 12:00:05 box sshd[812]: Failed password for root", now)!;
        var iso = SyslogFileLogSource.Parse("2025-10-01T11:59:00.123456+00:00 box cron[7]: (root) CMD (run-parts)", now)!;

        Assert.Equal(("sshd", "Failed password for root", LogSeverity.Error), (classic.Source, classic.Message, classic.Severity));
        Assert.Equal(new DateTimeOffset(2025, 10, 1, 12, 0, 5, TimeSpan.Zero), classic.Time);
        Assert.Equal(("cron", LogSeverity.Info), (iso.Source, iso.Severity));
        Assert.Null(SyslogFileLogSource.Parse("garbage", now));
    }

    [Fact]
    public async Task ReadsTheNewestLinesFromTheLogFileWithinTheTimeRange()
    {
        var dir = Directory.CreateTempSubdirectory("pickle-syslog").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "syslog"), "Oct  1 08:00:00 box a[1]: old entry\nOct  1 12:00:00 box b[2]: recent warning: low disk\nOct  1 12:10:00 box c[3]: error happened\n");
            var source = new SyslogFileLogSource(dir, () => new DateTimeOffset(2025, 10, 1, 12, 30, 0, TimeSpan.Zero));

            var entries = await source.QueryAsync(new LogQuery { Since = TimeSpan.FromHours(1) });
            var errors = await source.QueryAsync(new LogQuery { Since = TimeSpan.FromHours(1), MinSeverity = LogSeverity.Error });

            Assert.Equal(["error happened", "recent warning: low disk"], entries.Select(e => e.Message));
            Assert.Equal(["error happened"], errors.Select(e => e.Message));
            Assert.Equal(["syslog"], source.Sources);
            Assert.StartsWith("Get-Content -Wait -Tail 100 ", source.FollowCommand(new LogQuery())!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParsesMacUnifiedLogLines()
    {
        const string output = """
            {"timestamp":"2025-10-01 12:00:00.123456+0000","messageType":"Error","processImagePath":"/usr/libexec/trustd","processID":412,"subsystem":"com.apple.trust","eventMessage":"cert evaluation failed"}
            {"timestamp":"2025-10-01 12:01:00.000000+0000","messageType":"Default","processImagePath":"/sbin/launchd","eventMessage":"service exited"}
            """;

        var entries = MacLogSource.Parse(output);

        Assert.Equal(["service exited", "cert evaluation failed"], entries.Select(e => e.Message));
        Assert.Equal(("trustd", LogSeverity.Error), (entries[1].Source, entries[1].Severity));
        Assert.Equal("com.apple.trust", entries[1].Fields["Subsystem"]);
    }

    [Theory]
    [InlineData("30m", 30 * 60)]
    [InlineData("2h", 2 * 3600)]
    [InlineData("7d", 7 * 86400)]
    [InlineData("1w", 7 * 86400)]
    public void SinceParses(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), LogQuery.ParseSince(text));

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("1x")]
    [InlineData("-5m")]
    public void SinceRejectsOtherText(string text) => Assert.Null(LogQuery.ParseSince(text));
}
