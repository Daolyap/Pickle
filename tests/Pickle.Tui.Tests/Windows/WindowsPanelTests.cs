using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Windows;
using static Pickle.Tui.Tests.Windows.PanelRunner;

namespace Pickle.Tui.Tests.Windows;

public sealed class WindowsPanelTests : IDisposable
{
    private readonly TestPickle _t = TestPickle.Create();
    private readonly FakeWingetService _winget = new();
    private readonly FakeWindowsUpdateService _wu = new();
    private readonly FakeTaskSchedulerService _tasks = new();
    private readonly List<(string Title, string Message)> _asked = [];
    private readonly List<(string Title, string Message)> _told = [];

    public WindowsPanelTests()
    {
        _t.Runtime.ServiceRegistry.Add<IWingetService>(_winget);
        _t.Runtime.ServiceRegistry.Add<IWindowsUpdateService>(_wu);
        _t.Runtime.ServiceRegistry.Add<ITaskSchedulerService>(_tasks);
        _winget.Installed.Add(new WingetPackage("Git.Git", "Git", "2.45.1", "2.46.0", "winget"));
        _winget.Installed.Add(new WingetPackage("Microsoft.PowerShell", "PowerShell 7", "7.4.5.0", null, "winget"));
        _winget.Catalog.Add(new WingetPackage("7zip.7zip", "7-Zip", null, "24.08", "winget"));
        _wu.Available.Add(Update("11111111-2222-3333-4444-555555555555", "Intel Display Driver", driver: true));
        _wu.Available.Add(Update("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9", "2026-09 Cumulative Update (KB5031455)", severity: "Critical"));
        _wu.Available.Add(Update("99999999-2222-3333-4444-555555555555", "Feature preview", optional: true));
    }

    public void Dispose() => _t.Dispose();

    private PanelContext Context => new() { Pickle = _t.Runtime };

    private static WindowsUpdateInfo Update(string id, string title, bool driver = false, bool optional = false, string? severity = null) =>
        new(id, title, WindowsKb(title), [driver ? "Drivers" : "Updates"], 1_048_576, false, false, driver, optional, severity, "About " + title, null, true);

    private static string? WindowsKb(string title) => title.Contains("KB", StringComparison.Ordinal) ? "KB5031455" : null;

    private T Hooked<T>(T panel, bool answer = true)
        where T : WindowsPanelBase
    {
        panel.ConfirmHook = (title, message) =>
        {
            _asked.Add((title, message));
            return answer;
        };
        panel.MessageHook = (title, message) => _told.Add((title, message));
        return panel;
    }

    [Fact]
    public void PluginRegistersWindowsOnlyPanelsWithKeys()
    {
        new WindowsPanelsPlugin().Initialize(_t.Runtime);
        var panels = _t.Runtime.Panels.All.Where(p => p.Id is "winget" or "updates" or "scheduler").ToList();
        Assert.Equal(3, panels.Count);
        Assert.All(panels, p => Assert.True(p.WindowsOnly));
        Assert.Equal("Alt+W", panels.Single(p => p.Id == "winget").DefaultKey);
        Assert.Equal("Alt+U", panels.Single(p => p.Id == "updates").DefaultKey);
        Assert.Equal("Alt+S", panels.Single(p => p.Id == "scheduler").DefaultKey);
    }

    [Fact]
    public void WingetPanelLoadsAndFilters()
    {
        using var panel = Hooked(new WingetPanel(Context));
        var screen = Run(
            panel,
            Wait(() => panel.InstalledRows.Count == 2 && panel.UpgradeRows.Count == 1 && panel.BackendText.Contains("module", StringComparison.Ordinal)),
            Do(() =>
            {
                Assert.False(panel.InstallModuleVisible);
                Assert.Equal(2, panel.FilteredCount);
                panel.Filter = "powershell";
                panel.ApplyFilter();
                Assert.Equal(1, panel.FilteredCount);
                Assert.NotNull(panel.Updates);
            }));
        Assert.Contains("Installed", screen, StringComparison.Ordinal);
        Assert.Contains("Upgrades", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void WingetPanelUpgradesSelectedPackages()
    {
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            When(() => panel.UpgradeRows.Count == 1, () => panel.UpgradeSelected()),
            Wait(() => panel.UpgradeLog.Contains('✓', StringComparison.Ordinal)));

        Assert.Contains("upgrade Git.Git", _winget.Calls);
        Assert.Contains(_asked, a => a.Message.Contains("Git  2.45.1 → 2.46.0", StringComparison.Ordinal));
    }

    [Fact]
    public void WingetPanelSearchesShowsDetailsAndInstalls()
    {
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            Do(() =>
            {
                panel.Query = "7zip";
                panel.Search();
            }),
            When(() => panel.SearchResults.Count == 1, () => panel.ShowDetails(0)),
            When(() => panel.DetailsText.Contains("7-Zip description", StringComparison.Ordinal), () =>
            {
                Assert.Equal("24.08", panel.Version);
                panel.Scope = WingetScope.Machine;
                panel.InstallSelected();
            }),
            Wait(() => _winget.Calls.Contains("install 7zip.7zip")));

        Assert.Contains(_asked, a => a.Message.Contains("UAC", StringComparison.Ordinal));
    }

    [Fact]
    public void WingetPanelOffersModuleInstallAndSourceRepair()
    {
        _winget.Backend = WingetBackend.Cli;
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            When(() => panel.InstallModuleVisible, () =>
            {
                panel.InstallModule();
                panel.RepairSource();
            }),
            Wait(() => _winget.Calls.Contains("install-module") && _winget.Calls.Contains("repair-source elevated")));

        Assert.Contains(_asked, a => a.Message.Contains("Add-AppxPackage", StringComparison.Ordinal));
        Assert.Contains(_asked, a => a.Title.Contains("WinGet.Client", StringComparison.Ordinal));
    }

    [Fact]
    public void WingetPanelDeclinedConfirmationDoesNothing()
    {
        using var panel = Hooked(new WingetPanel(Context), answer: false);
        Run(
            panel,
            When(() => panel.UpgradeRows.Count == 1, () =>
            {
                panel.UpgradeSelected(all: true);
                panel.RepairSource();
            }));
        Assert.DoesNotContain(_winget.Calls, c => c.StartsWith("upgrade", StringComparison.Ordinal) || c.StartsWith("repair", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdatesPanelShowsStatusGroupsAndInstallsSelection()
    {
        _wu.Status = _wu.Status with { RebootRequired = true, IsManagedByOrganization = true, ManagedReason = "WSUS" };
        using var panel = Hooked(new UpdatesPanel(Context));
        var updates = panel.Updates!;
        Run(
            panel,
            When(() => updates.StatusText.Contains("RESTART REQUIRED", StringComparison.Ordinal), updates.Check),
            When(() => updates.Rows.Count == 2, () =>
            {
                Assert.Equal("Critical", updates.Rows[0].Update.Severity);
                Assert.True(updates.Rows[0].Selected);
                Assert.True(updates.Rows[1].Update.IsDriver);
                Assert.False(updates.Rows[1].Selected);
                updates.InstallSelected();
            }),
            Wait(() => updates.LogText.Contains("Installed", StringComparison.Ordinal)),
            Wait(() => panel.History!.Count == 0));

        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], _wu.Installed);
        Assert.Contains(_asked, a => a.Message.Contains("UAC", StringComparison.Ordinal));
        Assert.Contains("Managed by your organization: WSUS", updates.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatesPanelWithoutSupportShowsAMessage()
    {
        _t.Runtime.ServiceRegistry.Add<IWindowsUpdateService>(new FakeWindowsUpdateService { IsSupported = false });
        using var panel = new UpdatesPanel(Context);
        Assert.Null(panel.Updates);
        var screen = Run(panel, Do(() => { }));
        Assert.Contains("only available on Windows", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerPanelListsTasksAndActs()
    {
        _tasks.Tasks.Add(Task(@"\Pickle", "backup"));
        _tasks.Tasks.Add(Task(@"\Pickle\Test", "nested"));
        using var panel = Hooked(new SchedulerPanel(Context));
        Run(
            panel,
            Wait(() => panel.Tasks.Count == 1 && panel.Folders.Contains(@"\Pickle\Test")),
            Wait(() => panel.DetailsText.Contains("daily at 09:00", StringComparison.Ordinal)),
            Do(() =>
            {
                Assert.Contains(@"\", panel.Folders);
                panel.Act("run");
                panel.Act("toggle");
            }),
            Wait(() => _tasks.Calls.Contains(@"disable \Pickle\backup")),
            Do(() => panel.SelectFolder(@"\Pickle\Test")),
            Wait(() => panel.Tasks.Count == 1 && panel.Tasks[0].Name == "nested"),
            Do(() => panel.Act("delete")),
            Wait(() => _tasks.Calls.Contains(@"delete \Pickle\Test\nested")));

        Assert.Contains(@"run \Pickle\backup", _tasks.Calls);
    }

    [Fact]
    public void SchedulerPanelCreatesTasksFromTheDialogValues()
    {
        using var panel = Hooked(new SchedulerPanel(Context));
        Run(
            panel,
            Do(() => panel.CreateTask(new NewTaskValues("nightly", NewTaskTrigger.Weekly, "18:30", "mon,fri", true, "pk upgrade --yes", string.Empty, Elevated: true))),
            Wait(() => _tasks.Calls.Contains(@"create \Pickle\nightly")),
            Do(() => panel.CreateTask(new NewTaskValues(" ", NewTaskTrigger.Daily, "09:00", string.Empty, true, "Get-Date", string.Empty, false))));

        var created = Assert.Single(_tasks.Tasks);
        Assert.True(created.RunElevated);
        Assert.Contains("-NoLogo -c \"pk upgrade --yes\"", created.Actions[0], StringComparison.Ordinal);
        Assert.Contains(_asked, a => a.Message.Contains("UAC", StringComparison.Ordinal));
        Assert.Contains(_told, m => m.Title == "Error" && m.Message.Contains("name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NewTaskTrigger.Daily, "09:00", "", "daily 09:00")]
    [InlineData(NewTaskTrigger.Weekly, "18:30", "mon,fri", "weekly mon,fri 18:30")]
    [InlineData(NewTaskTrigger.Monthly, "08:00", "1,15", "monthly 1,15 08:00")]
    [InlineData(NewTaskTrigger.Once, "2026-10-01 12:00", "", "once 2026-10-01 12:00")]
    [InlineData(NewTaskTrigger.Every, "", "30m", "every 30m")]
    [InlineData(NewTaskTrigger.AtLogon, "", "", "at logon")]
    [InlineData(NewTaskTrigger.AtStartup, "", "", "at startup")]
    [InlineData(NewTaskTrigger.OnIdle, "", "", "on idle")]
    public void NewTaskValuesBuildScheduleText(NewTaskTrigger trigger, string time, string days, string expected) =>
        Assert.Equal(expected, new NewTaskValues("t", trigger, time, days, true, "x", string.Empty, false).ScheduleText);

    [Fact]
    public void ProgramActionsKeepProgramAndArguments()
    {
        var definition = new NewTaskValues("t", NewTaskTrigger.AtStartup, string.Empty, string.Empty, false, @"C:\Tools\backup.exe", "--full", false)
            .ToDefinition(_tasks, null);
        Assert.Equal(new TaskActionSpec(@"C:\Tools\backup.exe", "--full"), definition.Action);
        Assert.True(definition.RunElevated);
        Assert.Equal(@"\Pickle", definition.Folder);
    }

    [Fact]
    public void QuotesPickleCommands()
    {
        Assert.Equal("\"Get-Date | Out-File \\\"C:\\x y\\a.txt\\\"\"", WindowsPanelBase.QuoteArgument("Get-Date | Out-File \"C:\\x y\\a.txt\""));
        Assert.Equal("Get-Date", WindowsPanelBase.QuoteArgument("Get-Date"));
    }

    private static ScheduledTaskInfo Task(string folder, string name) => new(
        folder + @"\" + name, name, folder, true, "Ready", DateTimeOffset.Now.AddHours(-3), DateTimeOffset.Now.AddHours(21), 0,
        "Pickle task", "me", ["daily at 09:00"], [@"C:\pickle.exe -NoLogo -c Get-Date"], false);
}
