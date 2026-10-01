using Pickle.Abstractions;
using Pickle.Modules.Notifier;
using Pickle.Testing;
using Pickle.Testing.Fakes;

namespace Pickle.Modules.Tests.Notifier;

public class NotifierTests
{
    private sealed class FakeSink(bool available = true, bool ok = true) : INotificationSink
    {
        public bool IsAvailable => available;

        public List<(string Title, string Body)> Sent { get; } = [];

        public Task<bool> NotifyAsync(string title, string body, CancellationToken cancellationToken)
        {
            Sent.Add((title, body));
            return Task.FromResult(ok);
        }
    }

    private static HookEvent Done(string line, int seconds, bool success = true) =>
        new(HookKind.PostExecute, line, "/tmp", null, success, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void LongCommandsNotifyAndShortOrIgnoredOnesDoNot()
    {
        var settings = new NotifierSettings();

        Assert.Equal(("Command finished", "dotnet build (45s)"), CommandNotifier.Decide(settings, Done("dotnet build", 45)));
        Assert.Equal(("Command failed", "make all (2m 5s)"), CommandNotifier.Decide(settings, Done("make all", 125, success: false)));
        Assert.Null(CommandNotifier.Decide(settings, Done("dotnet build", 5)));
        Assert.Null(CommandNotifier.Decide(settings, Done("vim notes.md", 600)));
        Assert.Null(CommandNotifier.Decide(settings, Done("SSH.exe host", 600)));
        Assert.Null(CommandNotifier.Decide(settings, new HookEvent(HookKind.PostExecute, null, "/", null, true, TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void SettingsChangeTheThresholdAndRestrictToFailures()
    {
        var settings = new NotifierSettings { MinSeconds = 5, OnlyFailures = true };

        Assert.Null(CommandNotifier.Decide(settings, Done("make", 10)));
        Assert.NotNull(CommandNotifier.Decide(settings, Done("make", 10, success: false)));
        Assert.Null(CommandNotifier.Decide(settings, Done("make", 2, success: false)));
    }

    [Fact]
    public void LongCommandLinesAreShortened()
    {
        var (_, body) = CommandNotifier.Decide(new NotifierSettings(), Done(new string('x', 200), 60))!.Value;

        Assert.True(body.Length < 100);
        Assert.Contains("…", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABellRingsWhenThereIsNoNotifierOrItFails()
    {
        using var t = TestPickle.Create();
        var rung = new List<string>();

        await new CommandNotifier(t.Runtime.Config, new FakeSink(available: false), rung.Add).OnFinishedAsync(Done("make", 60), CancellationToken.None);
        await new CommandNotifier(t.Runtime.Config, new FakeSink(ok: false), rung.Add).OnFinishedAsync(Done("make", 60), CancellationToken.None);
        var ok = new FakeSink();
        await new CommandNotifier(t.Runtime.Config, ok, rung.Add).OnFinishedAsync(Done("make", 60), CancellationToken.None);

        Assert.Equal(["\u0007", "\u0007"], rung);
        Assert.Single(ok.Sent);
    }

    [Fact]
    public async Task LinuxUsesNotifySendWithTheTextAsSeparateArguments()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new FakeProgramRunner().On("notify-send", "--app-name=Pickle", string.Empty);
        var sink = new SystemNotificationSink(() => runner);

        var sent = await sink.NotifyAsync("Command finished", "rm -rf $(x) -- evil", CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(["--app-name=Pickle -- Command finished rm -rf $(x) -- evil"], runner.CommandLines("notify-send"));
        Assert.False(new SystemNotificationSink(() => new FakeProgramRunner()).IsAvailable);
    }

    [Fact]
    public void ThePkCommandReportsTheSettingsAndTestsTheSink()
    {
        var runner = new FakeProgramRunner().On("notify-send", "--app-name=Pickle", string.Empty);
        using var t = ModuleTestSupport.Start("notifier", runner);

        var status = t.Run("pk notify status");
        t.Run("pk notify test");

        Assert.Contains("30s", t.Terminal.GetScreenText(), StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            Assert.Single(runner.CommandLines("notify-send"));
        }

        _ = status;
    }
}
