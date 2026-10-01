using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests.Admin;

public sealed class LogsTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create(start: true, plugins: [new AdminPanelsPlugin()]);
    private readonly FakeLogSource _logs = new();

    public LogsTests()
    {
        var now = new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);
        _logs.Entries.AddRange(
        [
            new LogEntry(now, LogSeverity.Error, "sshd", "Failed password for root from 10.0.0.9") { Fields = new Dictionary<string, string> { ["Unit"] = "ssh.service" } },
            new LogEntry(now.AddMinutes(-1), LogSeverity.Info, "systemd", "Started Daily apt download activities."),
            new LogEntry(now.AddMinutes(-2), LogSeverity.Warning, "kernel", "disk almost full"),
        ]);
        _t.Runtime.Services.Add<ILogSource>(_logs);
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ThePanelShowsEntriesAndTheFilterSearchesMessages()
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = "password" };
        var panel = new LogsPanel(context);
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 3 && panel.List.Selected?.Source == "sshd")
            .WaitFor("details", _ => panel.Details.PlainText.Contains("ssh.service", StringComparison.Ordinal) && panel.Details.PlainText.Contains("Failed password", StringComparison.Ordinal))
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
    }

    [Fact]
    public void SeverityKeyReloadsWithAHigherMinimumAndFollowHandsOverTheCommand()
    {
        var context = new PanelContext { Pickle = _t.Runtime };
        var panel = new LogsPanel(context);
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 3)
            .Press(Key.F6)
            .WaitFor("warnings", _ => panel.List.TotalCount == 2)
            .Press(Key.F6)
            .WaitFor("errors", _ => panel.List.TotalCount == 1)
            .Press(Key.F8);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(LogSeverity.Error, _logs.Queries[^1].MinSeverity);
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "journalctl -f (default)"), context.Result);
    }

    [Fact]
    public void ListPrintsFilteredObjects()
    {
        Assert.Equal(["sshd"], _t.Run("pk logs list --level error | ForEach-Object Source"));
        Assert.Equal(["kernel"], _t.Run("pk logs list disk | ForEach-Object Source"));
    }

    [Fact]
    public void BadRangesAndLevelsAreUsageErrors()
    {
        Assert.Throws<InvalidOperationException>(() => _t.Run("pk logs list --since yesterday"));
        Assert.Throws<InvalidOperationException>(() => _t.Run("pk logs list --level loud"));
    }
}
