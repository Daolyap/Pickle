using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Admin;
using Terminal.Gui.Input;

namespace Pickle.Tui.Tests.Admin;

public sealed class TimersTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create(start: true, plugins: [new AdminPanelsPlugin()]);
    private readonly FakeJobScheduler _scheduler = new();

    public TimersTests()
    {
        _scheduler.Jobs.AddRange(
        [
            new ScheduledJob("cron:2", "morning", JobKind.Cron, "daily at 09:00", "pk upgrade --yes", true) { Managed = true },
            new ScheduledJob("cron:5", "line 5: backup", JobKind.Cron, "daily at 02:00", "backup", false),
            new ScheduledJob("fstrim.timer", "fstrim", JobKind.SystemdTimer, "weekly", "fstrim.service", true) { Scope = "system" },
        ]);
        _t.Runtime.Services.Add<IJobScheduler>(_scheduler);
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ThePanelListsJobsGroupedByKindAndScope()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var panel = new TimersPanel(new PanelContext { Pickle = _t.Runtime });
        var script = new UiScript()
            .WaitFor("loaded", _ => panel.List.TotalCount == 3)
            .Do("categories", _ => Assert.Equal(["cron (user)", "cron (user)", "systemd timer (system)"], panel.List.VisibleItems.Select(j => $"{TimersPanel.Label(j.Kind)} ({j.Scope})")))
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
    }

    [Fact]
    public void DeleteOnlyActsOnJobsPickleCreatedAndAsksFirst()
    {
        var panel = new TimersPanel(new PanelContext { Pickle = _t.Runtime, Argument = "backup" });
        var script = new UiScript()
            .WaitFor("selected", _ => panel.List.Selected?.Name == "line 5: backup")
            .Press(Key.F4)
            .Press(Key.Esc);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Empty(_scheduler.Calls);

        var morning = new TimersPanel(new PanelContext { Pickle = _t.Runtime, Argument = "morning" });
        var confirm = new UiScript()
            .WaitFor("selected", _ => morning.List.Selected?.Name == "morning")
            .Press(Key.F4)
            .WaitFor("dialog", app => app.TopRunnableView is Terminal.Gui.Views.Dialog)
            .Press(Key.Y)
            .WaitFor("deleted", _ => _scheduler.Calls.Count == 1)
            .Press(Key.Esc);
        TuiHarness.Run(morning, confirm);

        confirm.AssertOk();
        Assert.Equal(["delete morning"], _scheduler.Calls);
    }

    [Fact]
    public void EnableDisableAndHistory()
    {
        var context = new PanelContext { Pickle = _t.Runtime, Argument = "backup" };
        var panel = new TimersPanel(context);
        var script = new UiScript()
            .WaitFor("selected", _ => panel.List.Selected?.Name == "line 5: backup")
            .Press(Key.F3)
            .WaitFor("toggled", _ => _scheduler.Calls.Count == 1)
            .Press(Key.F8);

        TuiHarness.Run(panel, script);

        script.AssertOk();
        Assert.Equal(["enable line 5: backup"], _scheduler.Calls);
        Assert.Equal(new PanelResult(PanelResultKind.RunCommand, "journalctl -u line 5: backup"), context.Result);
    }

    [Fact]
    public void CommandListsAddsAndOperatesByName()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(["fstrim", "line 5: backup", "morning"], _t.Run("pk timers list | ForEach-Object Name").Order());

        _t.Run("pk timers add 'every 30m' 'echo hi' --name ping --kind cron");
        _t.Run("pk timers disable morning");
        _t.Run("pk timers run morning --yes");
        _t.Run("pk schedule remove morning --yes");

        Assert.Equal(
            ["create Cron ping [Interval] echo hi", "disable morning", "run morning", "delete morning"],
            _scheduler.Calls);
        Assert.Throws<InvalidOperationException>(() => _t.Run("pk timers add 'whenever' 'echo hi'"));

        _t.Run("pk timers run nope --yes");
        Assert.Contains("No job called 'nope'", _t.Terminal.GetScreenText(), StringComparison.Ordinal);
    }
}
