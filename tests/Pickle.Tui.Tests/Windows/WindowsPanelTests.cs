using System.Drawing;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Testing;
using Pickle.Testing.Fakes;
using Pickle.Tui.Panels.Windows;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
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
    private readonly List<(string Title, string Message, string[] Buttons)> _choices = [];
    private int _choice;
    private string? _typed;

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
        panel.ChoiceHook = (title, message, buttons) =>
        {
            _choices.Add((title, message, buttons));
            return _choice;
        };
        panel.PromptHook = (_, _) => _typed;
        return panel;
    }

    private void AddUpgradable(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            _winget.Installed.Add(new WingetPackage($"Vendor.App{i}", $"App {i}", "1.0", "2.0", "winget"));
        }
    }

    /// <summary>Viewport row of table row <paramref name="row"/> (the header takes the first lines).</summary>
    private static int RowY<T>(SelectionTable<T> table, int row)
        where T : class
    {
        for (var y = 0; y < table.Viewport.Height; y++)
        {
            if (table.ScreenToCell(3, y) is { } cell && cell.Y == row)
            {
                return y;
            }
        }

        throw new InvalidOperationException($"row {row} is not visible");
    }

    private static void Click<T>(SelectionTable<T> table, int row, MouseFlags modifiers = MouseFlags.None)
        where T : class =>
        table.NewMouseEvent(new Mouse { Position = new Point(3, RowY(table, row)), Flags = MouseFlags.LeftButtonClicked | modifiers });

    private static void ClickHeader<T>(SelectionTable<T> table)
        where T : class
    {
        for (var y = 0; y < table.Viewport.Height; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                table.ScreenToCell(x, y, out var header);
                if (header == 0)
                {
                    table.NewMouseEvent(new Mouse { Position = new Point(x, y), Flags = MouseFlags.LeftButtonClicked });
                    return;
                }
            }
        }

        throw new InvalidOperationException("no header");
    }

    private static bool[] Ticks<T>(SelectionTable<T> table)
        where T : class => [.. table.Items.Select(table.IsMarked)];

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
                panel.RepairSource(elevated: true);
            }),
            Wait(() => _winget.Calls.Contains("install-module") && _winget.Calls.Contains("repair-source") && _winget.Calls.Contains("repair-source elevated")));

        Assert.Contains(_asked, a => a.Message.Contains("Add-AppxPackage", StringComparison.Ordinal) && a.Message.Contains("no administrator rights", StringComparison.Ordinal));
        Assert.Contains(_asked, a => a.Message.Contains("UAC", StringComparison.Ordinal));
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
                Assert.Equal("Critical", updates.Rows[0].Severity);
                Assert.True(updates.IsSelected(updates.Rows[0]));
                Assert.True(updates.Rows[1].IsDriver);
                Assert.False(updates.IsSelected(updates.Rows[1]));
                updates.InstallSelected();
            }),
            Wait(() => updates.LogText.Contains("Installed", StringComparison.Ordinal)),
            Wait(() => panel.History!.Count == 0));

        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], _wu.Installed);
        Assert.Contains(_asked, a => a.Message.Contains("UAC", StringComparison.Ordinal));
        Assert.Contains("Managed by your organization: WSUS", updates.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeSelectedUsesInstalledSelectionOnlyOnTheInstalledTab()
    {
        _winget.Installed.Add(new WingetPackage("7zip.7zip", "7-Zip", "23.01", "24.08", "winget"));
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            Wait(() => panel.InstalledRows.Count == 3 && panel.UpgradeRows.Count == 2),
            Do(() =>
            {
                // Nothing ticked in Installed: the Upgrades list (all ticked) wins, whatever tab is visible.
                panel.ActiveTab = "Installed";
                Assert.Equal(["Git.Git", "7zip.7zip"], panel.UpgradeTargets().Packages.Select(p => p.Id));

                var installed = panel.InstalledTable;
                installed.SetMarked(installed.Items.Single(p => p.Id == "Git.Git"), true);
                installed.SetMarked(installed.Items.Single(p => p.Id == "Microsoft.PowerShell"), true);
                var (packages, from, skipped) = panel.UpgradeTargets();
                Assert.Equal(["Git.Git"], packages.Select(p => p.Id));
                Assert.Equal("selected in Installed", from);
                Assert.Equal(1, skipped);

                panel.ActiveTab = "Upgrades";
                Assert.Equal(["Git.Git", "7zip.7zip"], panel.UpgradeTargets().Packages.Select(p => p.Id));

                panel.ActiveTab = "Installed";
                panel.UpgradeSelected();
            }),
            Wait(() => panel.UpgradeLog.Contains('✓', StringComparison.Ordinal)));

        Assert.Equal(["upgrade Git.Git"], _winget.Calls);
        Assert.Contains(_asked, a => a.Message.Contains("1 package(s) selected in Installed", StringComparison.Ordinal) && a.Message.Contains("1 selected package(s) have no upgrade", StringComparison.Ordinal));
    }

    [Fact]
    public void ShiftClickTicksAndUnticksARangeOfUpgrades()
    {
        AddUpgradable(4);
        using var panel = Hooked(new WingetPanel(Context));
        var table = panel.UpgradesTable;
        Run(
            panel,
            When(() => panel.UpgradeRows.Count == 5, () => panel.ActiveTab = "Upgrades"),
            Do(() => Assert.All(Ticks(table), Assert.True)),
            Do(() =>
            {
                Click(table, 1);
                Click(table, 3, MouseFlags.Shift);
                Assert.Equal([true, false, false, false, true], Ticks(table));

                // Alt+click is the same gesture for terminals that keep Shift+click for text selection.
                Click(table, 0);
                Click(table, 2, MouseFlags.Alt);
                Assert.Equal([false, false, false, false, true], Ticks(table));

                Click(table, 2);
                Click(table, 4, MouseFlags.Shift);
                Assert.Equal([false, false, true, true, true], Ticks(table));
                Assert.Equal(4, table.CursorRow);

                ClickHeader(table);
                Assert.All(Ticks(table), Assert.True);
                ClickHeader(table);
                Assert.All(Ticks(table), Assert.False);
            }));
    }

    [Fact]
    public void KeyboardTicksRangesAndAll()
    {
        AddUpgradable(4);
        using var panel = Hooked(new WingetPanel(Context));
        var table = panel.UpgradesTable;
        Run(
            panel,
            When(() => panel.UpgradeRows.Count == 5, () => panel.ActiveTab = "Upgrades"),
            Do(() =>
            {
                table.SetFocus();
                table.NewKeyDownEvent(Key.Space);
                Assert.Equal([false, true, true, true, true], Ticks(table));
                Assert.Equal(1, table.CursorRow);

                table.NewKeyDownEvent(Key.CursorUp);
                table.NewKeyDownEvent(Key.Space);
                Assert.Equal([true, true, true, true, true], Ticks(table));

                // The anchor (row 0) is ticked again; untick it and extend the untick downwards.
                table.NewKeyDownEvent(Key.CursorUp);
                table.NewKeyDownEvent(Key.Space);
                table.NewKeyDownEvent(Key.CursorUp);
                table.NewKeyDownEvent(Key.CursorDown.WithShift);
                table.NewKeyDownEvent(Key.CursorDown.WithShift);
                Assert.Equal([false, false, false, true, true], Ticks(table));

                table.NewKeyDownEvent(Key.CursorDown);
                table.NewKeyDownEvent(Key.CursorDown);
                table.NewKeyDownEvent(Key.Space.WithShift);
                Assert.Equal([false, false, false, false, false], Ticks(table));

                table.NewKeyDownEvent(Key.A.WithCtrl);
                Assert.All(Ticks(table), Assert.True);
            }));
    }

    [Fact]
    public void RefreshKeepsUntickedUpgrades()
    {
        AddUpgradable(2);
        using var panel = Hooked(new WingetPanel(Context));
        var table = panel.UpgradesTable;
        Run(
            panel,
            When(() => panel.UpgradeRows.Count == 3, () =>
            {
                table.SetMarked(table.Items.Single(p => p.Id == "Git.Git"), false);
                _winget.Installed.Add(new WingetPackage("Vendor.New", "New app", "1.0", "1.1", "winget"));
                panel.Refresh();
            }),
            Wait(() => panel.UpgradeRows.Count == 4),
            Do(() =>
            {
                Assert.False(table.IsMarked(table.Items.Single(p => p.Id == "Git.Git")));
                Assert.True(table.IsMarked(table.Items.Single(p => p.Id == "Vendor.New")));
                Assert.Equal(["Vendor.App1", "Vendor.App2", "Vendor.New"], panel.UpgradeTargets().Packages.Select(p => p.Id).Order());
            }));
    }

    [Fact]
    public void UninstallSelectedConfirmsWithTheListAndCanElevate()
    {
        using var panel = Hooked(new WingetPanel(Context));
        var installed = panel.InstalledTable;
        Run(
            panel,
            When(() => panel.InstalledRows.Count == 2, () =>
            {
                panel.ActiveTab = "Installed";
                Click(installed, 0);
                Click(installed, 1, MouseFlags.Shift);
                Assert.Equal(2, installed.Marked.Count);

                _choice = -1;
                panel.UninstallSelected();
                Assert.Empty(_winget.Calls);

                _choice = 0;
                panel.UninstallSelected();
            }),
            Wait(() => _winget.Calls.Count == 2 && panel.InstalledLog.Contains("✓ uninstall Microsoft.PowerShell ok", StringComparison.Ordinal)),
            Do(() =>
            {
                _choice = 1;
                installed.SetMarked(installed.Items[0], true);
                installed.SetMarked(installed.Items[1], false);
                panel.UninstallSelected();
            }),
            Wait(() => _winget.Calls.Count == 3));

        Assert.Equal(["uninstall Git.Git", "uninstall Microsoft.PowerShell", "uninstall-elevated Git.Git"], _winget.Calls);
        var question = _choices[0];
        Assert.Contains("Git  2.45.1  [Git.Git]", question.Message, StringComparison.Ordinal);
        Assert.Contains("PowerShell 7  7.4.5.0  [Microsoft.PowerShell]", question.Message, StringComparison.Ordinal);
        Assert.Equal(["_Uninstall", "As _administrator", "_Cancel"], question.Buttons);
    }

    [Fact]
    public void UninstallWithoutTicksUsesTheCursorRowAndShowsFailures()
    {
        _winget.Result = call => call == "uninstall Git.Git"
            ? new WingetOperationResult(false, "uninstall Git.Git: the installer reported an error.", 1603) { Output = "MSI (s) error 1730: You must be an Administrator" }
            : null;
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            When(() => panel.InstalledRows.Count == 2, () =>
            {
                panel.ActiveTab = "Installed";
                Assert.Equal(["Git.Git"], panel.UninstallTargets().Select(p => p.Id));
                panel.UninstallSelected();
            }),
            Wait(() => panel.InstalledLog.Contains("As administrator", StringComparison.Ordinal)));

        Assert.Contains("│ MSI (s) error 1730", panel.InstalledLog, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchInstallsFromTheKeyboard()
    {
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            Do(() =>
            {
                panel.ActiveTab = "Search";
                panel.QueryField.SetFocus();
            }),
            Do(() =>
            {
                foreach (var ch in "7zip")
                {
                    panel.App!.Keyboard.RaiseKeyDownEvent(new Key(ch));
                }

                panel.App!.Keyboard.RaiseKeyDownEvent(Key.Enter);
            }),
            Wait(() => panel.SearchResults.Count == 1 && panel.SearchTable.HasFocus && panel.DetailsText.Contains("7-Zip description", StringComparison.Ordinal)),
            Do(() => panel.App!.Keyboard.RaiseKeyDownEvent(Key.I)),
            Wait(() => _winget.Calls.Contains("install 7zip.7zip")),
            Do(() => panel.App!.Keyboard.RaiseKeyDownEvent(Key.Enter)),
            Wait(() => _winget.Calls.Count(c => c == "install 7zip.7zip") == 2));

        Assert.Equal(2, _asked.Count(a => a.Title == "Install package" && a.Message.Contains("7-Zip [7zip.7zip]", StringComparison.Ordinal)));
    }

    [Fact]
    public void RepairShowsTheFullOutput()
    {
        _winget.Result = call => call == "repair-source"
            ? new WingetOperationResult(false, "Repairing the winget source failed: The source package is in use. (0x80073D02)", unchecked((int)0x80073D02))
            {
                Output = "Add-AppxPackage : Deployment failed with HRESULT: 0x80073D02\nGet-AppxLog: close WindowsPackageManagerServer.exe",
            }
            : null;
        using var panel = Hooked(new WingetPanel(Context));
        Run(
            panel,
            Do(() => panel.RepairSource()),
            Wait(() => panel.RepairOutput.Contains("0x80073D02", StringComparison.Ordinal)));

        Assert.StartsWith("✗ Repairing the winget source failed", panel.RepairOutput, StringComparison.Ordinal);
        Assert.Contains("close WindowsPackageManagerServer.exe", panel.RepairOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatesPanelSelectsRangesAndKeepsTicksAcrossChecks()
    {
        _wu.Available.Add(Update("33333333-2222-3333-4444-555555555555", "Realtek Audio Driver", driver: true));
        using var panel = Hooked(new UpdatesPanel(Context));
        var updates = panel.Updates!;
        Run(
            panel,
            Do(updates.Check),
            When(() => updates.Rows.Count == 3, () =>
            {
                Assert.Equal([true, false, false], Ticks(updates.Table));
                Click(updates.Table, 1);
                Click(updates.Table, 2, MouseFlags.Shift);
                Assert.Equal([true, true, true], Ticks(updates.Table));
                Click(updates.Table, 0);
                updates.Check();
            }),
            Wait(() => _wu.Queries.Count == 2),
            Do(() =>
            {
                Assert.Equal([false, true, true], Ticks(updates.Table));
                updates.InstallSelected();
            }),
            Wait(() => _wu.Installed.Count == 2));

        Assert.Equal(["11111111-2222-3333-4444-555555555555", "33333333-2222-3333-4444-555555555555"], _wu.Installed.Order());
    }

    [Fact]
    public void UpdatesPanelInstallsAKbByNumber()
    {
        using var panel = Hooked(new UpdatesPanel(Context));
        var updates = panel.Updates!;
        Run(
            panel,
            Do(() =>
            {
                _typed = "nope";
                updates.InstallKb();
                _typed = "KB5000001";
                updates.InstallKb();
            }),
            Wait(() => _told.Any(t => t.Message.Contains("KB5000001 is not offered", StringComparison.Ordinal))),
            Do(() =>
            {
                _typed = null;
                updates.InstallKb();
                _typed = " 5031455 ";
                updates.InstallKb();
            }),
            Wait(() => _wu.Installed.Count == 1));

        Assert.Equal(["0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"], _wu.Installed);
        Assert.Contains(_told, t => t.Message.Contains("'nope' is not a KB number", StringComparison.Ordinal));
        Assert.All(_wu.Queries, q => Assert.True(q.IncludeOptional && q.IncludeDrivers));
        Assert.Contains(_asked, a => a.Message.Contains("KB5031455  2026-09 Cumulative Update", StringComparison.Ordinal));
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
