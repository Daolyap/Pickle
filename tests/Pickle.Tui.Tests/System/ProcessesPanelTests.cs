using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.SystemMonitoring;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Views;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.SystemMonitoring;

public sealed class ProcessesPanelTests : IDisposable
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Now.AddHours(-1);

    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeProcessMonitor _monitor = new();
    private readonly List<(string Title, string Message)> _asked = [];
    private readonly List<(string Title, string Message)> _told = [];

    public ProcessesPanelTests()
    {
        _t.Runtime.ServiceRegistry.Add<IProcessMonitor>(_monitor);
        _monitor.Processes.Add(new ProcessSample(1, "init", 0.1, 8 << 20, 1, Started, "root"));
        _monitor.Processes.Add(new ProcessSample(300, "firefox", 42.5, 900 << 20, 80, Started, "alice", 1));
        _monitor.Processes.Add(new ProcessSample(301, "firefox-tab", 3.0, 200 << 20, 20, Started, "alice", 300));
        _monitor.Processes.Add(new ProcessSample(302, "Web Content", 7.0, 150 << 20, 25, Started, "alice", 301));
        _monitor.Processes.Add(new ProcessSample(400, "bash", 0.0, 4 << 20, 1, Started, "alice", 1));
    }

    public void Dispose() => _t.Dispose();

    private ProcessesPanel Panel(bool answer = true, string? argument = null)
    {
        var panel = new ProcessesPanel(new PanelContext { Pickle = _t.Runtime, Argument = argument })
        {
            ConfirmHook = (title, message) =>
            {
                _asked.Add((title, message));
                return answer;
            },
            MessageHook = (title, message) => _told.Add((title, message)),
        };
        return panel;
    }

    private static bool Loaded(ProcessesPanel panel) => panel.Table.TotalCount == 5;

    [Fact]
    public void ShowsTotalsAndProcessesByCpu()
    {
        using var panel = Panel();
        var screen = SystemPanelRunner.RunAndDraw(panel, Wait(() => Loaded(panel)));

        Assert.Equal([300, 302, 301, 1, 400], panel.Table.Rows.Select(p => p.Id));
        Assert.Contains("CPU 25.0%", panel.Meters.PlainText, StringComparison.Ordinal);
        Assert.Contains("Memory 4.00 GB / 16.0 GB", panel.Meters.PlainText, StringComparison.Ordinal);
        Assert.StartsWith("5 processes · 127 threads · sorted by CPU % ▼", panel.SummaryText, StringComparison.Ordinal);
        Assert.Contains("firefox", screen, StringComparison.Ordinal);
        Assert.Contains("CPU % ▼", screen, StringComparison.Ordinal);
        Assert.Contains("900 MB", screen, StringComparison.Ordinal);
        Assert.Contains("Del End", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void SortsByAnyColumnAndReverses()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => Loaded(panel), () =>
            {
                panel.Table.SortBy(0);
                Assert.Equal(["bash", "firefox", "firefox-tab", "init", "Web Content"], panel.Table.Rows.Select(p => p.Name));
                panel.Table.SortBy(0);
                Assert.Equal("Web Content", panel.Table.Rows[0].Name);
                panel.Table.SortBy(1);
                Assert.Equal([400, 302, 301, 300, 1], panel.Table.Rows.Select(p => p.Id));
                Assert.True(panel.Table.HandleKey(Key.F6.WithShift));
                Assert.Equal(1, panel.Table.Rows[0].Id);
                Assert.True(panel.Table.HandleKey(Key.F6));
                Assert.Equal("User ▲", panel.Table.SortDescription);
                panel.Table.SortBy(4);
                Assert.Equal(300, panel.Table.Rows[0].Id);
            }));
    }

    [Fact]
    public void TypingFiltersWithTheFuzzyMatcherAndEscClears()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => Loaded(panel), () =>
            {
                foreach (var ch in "frfx")
                {
                    panel.App!.InjectKey(new Key(ch));
                }
            }),
            When(() => panel.Table.Rows.Count == 2, () =>
            {
                Assert.Equal(["firefox", "firefox-tab"], panel.Table.Rows.Select(p => p.Name).Order());
                panel.App!.InjectKey(Key.Backspace);
                panel.App!.InjectKey(Key.Backspace);
                panel.App!.InjectKey(Key.Backspace);
            }),
            When(() => panel.Table.FilterText == "f", () => panel.App!.InjectKey(Key.Esc)),
            Wait(() => panel.Table.FilterText.Length == 0 && panel.Table.Rows.Count == 5 && !panel.IsClosed));
    }

    [Fact]
    public void FilterCanComeFromThePanelArgument()
    {
        using var panel = Panel(argument: "bash");
        Run(panel, Wait(() => panel.Table.TotalCount == 5));
        Assert.Equal([400], panel.Table.Rows.Select(p => p.Id));
    }

    [Fact]
    public void RefreshKeepsTheSelectedProcess()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => Loaded(panel), () => panel.Table.Select(p => p.Id == 301)),
            Do(() =>
            {
                lock (_monitor.Processes)
                {
                    _monitor.Processes.RemoveAll(p => p.Id == 302);
                    _monitor.Processes.Add(new ProcessSample(302, "Web Content", 90, 150 << 20, 25, Started, "alice", 301));
                    _monitor.Processes.Add(new ProcessSample(500, "new", 0, 1 << 20, 1, Started, "alice", 1));
                }

                panel.Refresh();
            }),
            Wait(() => panel.Table.TotalCount == 6),
            Do(() =>
            {
                Assert.Equal(302, panel.Table.Rows[0].Id);
                Assert.Equal(301, panel.Table.Selected?.Id);
            }));
    }

    [Fact]
    public void EndProcessTreeConfirmsWithTheChildCountAndCallsTheMonitor()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => Loaded(panel), () =>
            {
                panel.Table.Select(p => p.Id == 300);
                panel.End(tree: true);
            }),
            Wait(() => panel.Table.TotalCount == 2));

        Assert.Equal(["kill-tree 300"], _monitor.Calls);
        var (title, message) = Assert.Single(_asked);
        Assert.Equal("End process tree", title);
        Assert.Contains("firefox (300) and its 2 child processes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclinedConfirmationEndsNothing()
    {
        using var panel = Panel(answer: false);
        Run(
            panel,
            When(() => Loaded(panel), () =>
            {
                panel.End(tree: false);
                panel.End(tree: true);
            }));

        Assert.Equal(2, _asked.Count);
        Assert.Empty(_monitor.Calls);
    }

    [Fact]
    public void DeleteKeyAsksInADialogBeforeEndingTheProcess()
    {
        var script = new UiScript()
            .WaitFor("loaded", app => app.TopRunnableView is ProcessesPanel panel && Loaded(panel))
            .Do("select bash", app => TuiHarness.Top<ProcessesPanel>(app).Table.Select(p => p.Id == 400))
            .Press(Key.Delete)
            .WaitFor("confirm", app => app.TopRunnableView is Dialog)
            .Press(Key.Y)
            .WaitFor("ended", app => app.TopRunnableView is ProcessesPanel { Table.TotalCount: 4 })
            .Press(Key.Esc);
        var (t, host) = TuiHarness.Start(script);
        using var _ = t;
        t.Runtime.ServiceRegistry.Add<IProcessMonitor>(_monitor);
        new SystemPanelsPlugin().Initialize(t.Runtime);

        Assert.Null(host.Show(ProcessesPanel.PanelId));
        script.AssertOk();
        Assert.Equal(["kill 400"], _monitor.Calls);
    }

    [Fact]
    public void PriorityIsPickedFromAList()
    {
        using var panel = Panel();
        var offered = new List<string>();
        panel.PickHook = (_, options) =>
        {
            offered.AddRange(options);
            return "Below normal";
        };
        Run(
            panel,
            When(() => Loaded(panel), () =>
            {
                panel.Table.Select(p => p.Id == 400);
                panel.ChangePriority();
            }),
            Wait(() => _monitor.Calls.Count == 1));

        Assert.Equal(["High", "Above normal", "Normal", "Below normal", "Idle"], offered);
        Assert.Equal("priority 400 BelowNormal", _monitor.Calls[0]);
    }

    [Fact]
    public void DetailsShowPathCommandLineAndParent()
    {
        using var panel = Panel();
        Run(
            panel,
            When(() => Loaded(panel), () =>
            {
                panel.Table.Select(p => p.Id == 300);
                panel.ShowDetails();
            }),
            Wait(() => _told.Count == 1));

        var (title, text) = _told[0];
        Assert.Equal("firefox (300)", title);
        Assert.Contains("Path:            /usr/bin/firefox", text, StringComparison.Ordinal);
        Assert.Contains("Parent:          init (1)", text, StringComparison.Ordinal);
        Assert.Contains("firefox --flag", text, StringComparison.Ordinal);
        Assert.Contains("Priority:        Normal", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAMonitorThePanelExplainsWhy()
    {
        using var t = TestPickle.Create();
        using var panel = new ProcessesPanel(new PanelContext { Pickle = t.Runtime });
        var screen = SystemPanelRunner.RunAndDraw(panel, Do(() => { }));
        Assert.Contains("not available", screen, StringComparison.Ordinal);
    }
}
