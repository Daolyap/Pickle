using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>
/// winget dashboard (Alt+W): Installed (filterable, multi-select, upgradable highlighted), Upgrades (ticked list),
/// Search (keyboard install with version and scope), Sources (repair with its output) and Windows Updates.
/// </summary>
public sealed class WingetPanel : WindowsPanelBase
{
    private const string SelectionHelp = "Space/click tick · Shift+click or Shift+↑↓ range · Ctrl+A all";
    private static readonly string[] ScopeLabels = ["Any", "User", "Machine"];

    private readonly IWingetService? _winget;
    private readonly Label _backend;
    private readonly Button _installModule;
    private readonly Tabs? _tabs;
    private readonly View _installedTab = new() { Title = "Installed", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly View _upgradesTab = new() { Title = "Upgrades", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly TextField _filter = new() { X = 8, Y = 0, Width = Dim.Fill() };
    private readonly SelectionTable<WingetPackage> _installedTable;
    private readonly SelectionTable<WingetPackage> _upgradesTable;
    private readonly TextPane _installedLog = MakeText("Progress · " + SelectionHelp);
    private readonly TextPane _upgradeLog = MakeText("Progress · " + SelectionHelp);
    private readonly TextField _query = new() { X = 8, Y = 0, Width = Dim.Fill(12) };
    private readonly TableView _searchTable = new() { Y = 2, Width = Dim.Percent(55), Height = Dim.Fill(), FullRowSelect = true };
    private readonly TextPane _searchDetails = MakeText("Details");
    private readonly TextField _version = new() { Width = 18 };
    private readonly OptionSelector _scope = new() { Labels = ScopeLabels, Orientation = Orientation.Horizontal, Value = 0 };
    private readonly TableView _sourcesTable = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Percent(40), FullRowSelect = true };
    private readonly TextPane _repairOutput = MakeText("Repair output");
    private readonly UpdatesView? _updates;
    private List<WingetPackage> _installed = [];
    private List<WingetPackage> _upgrades = [];
    private List<WingetPackage> _results = [];
    private WingetPackageDetails? _details;

    public WingetPanel(PanelContext context)
        : base(context, "winget")
    {
        _winget = Service<IWingetService>();
        _installedTable = new SelectionTable<WingetPackage>(
            p => p.Id,
            _ => false,
            ("Name", p => p.Name),
            ("Version", p => p.InstalledVersion),
            ("Available", p => p.AvailableVersion),
            ("Id", p => p.Id),
            ("Source", p => p.Source))
        { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(6) };
        _upgradesTable = new SelectionTable<WingetPackage>(
            p => p.Id,
            _ => true,
            ("Name", p => p.Name),
            ("Installed", p => p.InstalledVersion),
            ("Available", p => p.AvailableVersion),
            ("Id", p => p.Id))
        { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(6) };
        _backend = new Label { X = 0, Y = 0, Text = "Backend: detecting…" };
        _installModule = MakeButton("Install Microsoft.WinGet._Client", InstallModule);
        _installModule.X = Pos.Right(_backend) + 2;
        _installModule.Visible = false;
        if (_winget is not { IsSupported: true })
        {
            _backend.Text = "winget is only available on Windows.";
            Body.Add(_backend);
            return;
        }

        _tabs = new Tabs { Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _tabs.Add(BuildInstalledTab(), BuildUpgradesTab(), BuildSearchTab(), BuildSourcesTab());
        if (Service<IWindowsUpdateService>() is { IsSupported: true } wu)
        {
            _updates = new UpdatesView(this, wu) { Title = "Windows Updates" };
            _tabs.Add(_updates);
        }

        Body.Add(_backend, _installModule, _tabs);
        AddHint(Key.F5, "Refresh", Refresh);
        AddHint(Key.F8, "Uninstall selected", UninstallSelected);
        AddHint(Key.F9, "Upgrade selected", () =>
        {
            if (_updates is not null && ReferenceEquals(_tabs.Value, _updates))
            {
                _updates.InstallSelected();
            }
            else
            {
                UpgradeSelected();
            }
        });
    }

    internal IReadOnlyList<WingetPackage> InstalledRows => _installed;

    internal IReadOnlyList<WingetPackage> UpgradeRows => _upgrades;

    internal IReadOnlyList<WingetPackage> SearchResults => _results;

    internal string BackendText => _backend.Text;

    internal bool InstallModuleVisible => _installModule.Visible;

    internal string UpgradeLog => _upgradeLog.Content;

    internal string InstalledLog => _installedLog.Content;

    internal string DetailsText => _searchDetails.Content;

    internal string RepairOutput => _repairOutput.Content;

    internal SelectionTable<WingetPackage> InstalledTable => _installedTable;

    internal SelectionTable<WingetPackage> UpgradesTable => _upgradesTable;

    internal TableView SearchTable => _searchTable;

    internal TextField QueryField => _query;

    internal UpdatesView? Updates => _updates;

    internal string Filter
    {
        get => _filter.Text;
        set => _filter.Text = value;
    }

    internal string Query
    {
        get => _query.Text;
        set => _query.Text = value;
    }

    internal string Version
    {
        get => _version.Text;
        set => _version.Text = value;
    }

    internal WingetScope Scope
    {
        get => (_scope.Value ?? 0) switch
        {
            1 => WingetScope.User,
            2 => WingetScope.Machine,
            _ => WingetScope.Any,
        };
        set => _scope.Value = (int)value;
    }

    /// <summary>The visible tab: "Installed", "Upgrades", "Search", "Sources" or "Windows Updates".</summary>
    internal string ActiveTab
    {
        get => _tabs?.Value?.Title ?? string.Empty;
        set
        {
            if (_tabs?.TabCollection.FirstOrDefault(t => t.Title == value) is { } tab)
            {
                _tabs.Value = tab;
            }
        }
    }

    protected override void Opened()
    {
        Refresh();
        _updates?.Start();
    }

    internal void Refresh()
    {
        if (_winget is null)
        {
            return;
        }

        Load(_winget.GetBackendAsync, ApplyBackend, "detecting winget…");
        Load(_winget.ListInstalledAsync, ApplyInstalled, "loading packages…");
        var includeUnknown = Pickle.Config.Current.Winget.IncludeUnknownVersions;
        Load(ct => _winget.ListUpgradesAsync(includeUnknown, ct), ApplyUpgrades, "checking upgrades…");
        Load(_winget.ListSourcesAsync, ApplySources, "loading sources…");
    }

    internal void ApplyBackend(WingetBackend backend)
    {
        _backend.Text = "Backend: " + backend switch
        {
            WingetBackend.PowerShellModule => "Microsoft.WinGet.Client module",
            WingetBackend.Cli => "winget.exe (text output)",
            _ => "unavailable — install App Installer from the Microsoft Store",
        };
        _installModule.Visible = backend == WingetBackend.Cli;
        if (backend == WingetBackend.Cli && Pickle.Config.Current.Winget.AutoInstallClientModule)
        {
            InstallModule(askFirst: false);
        }
    }

    internal void ApplyInstalled(IReadOnlyList<WingetPackage> packages)
    {
        _installed = [.. packages.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        ApplyFilter();
    }

    internal void ApplyUpgrades(IReadOnlyList<WingetPackage> packages)
    {
        _upgrades = [.. packages];
        _upgradesTable.SetItems(_upgrades);
    }

    internal void ApplySources(IReadOnlyList<WingetSource> sources)
    {
        _sourcesTable.Table = new EnumerableTableSource<WingetSource>(sources, new Dictionary<string, Func<WingetSource, object>>
        {
            ["Name"] = s => s.Name,
            ["Argument"] = s => s.Argument,
            ["Type"] = s => s.Type,
        });
        _sourcesTable.Update();
    }

    internal void ApplyFilter()
    {
        var filter = _filter.Text.Trim();
        List<WingetPackage> rows = filter.Length == 0
            ? _installed
            : [.. _installed.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        _installedTable.SetItems(rows);
        var highlight = HighlightScheme();
        _installedTable.Style.RowColorGetter = args => args.RowIndex < rows.Count && rows[args.RowIndex].IsUpgradable ? highlight : null;
        FilteredCount = rows.Count;
    }

    internal int FilteredCount { get; private set; }

    /// <summary>
    /// What "Upgrade selected" upgrades: packages ticked in the Installed list while it is the visible tab (those
    /// with an upgrade), otherwise the ticked rows of the Upgrades list.
    /// </summary>
    internal (IReadOnlyList<WingetPackage> Packages, string From, int Skipped) UpgradeTargets(bool all = false)
    {
        if (all)
        {
            return (_upgrades, "all upgrades", 0);
        }

        if (ActiveTab == _installedTab.Title && _installedTable.Marked is { Count: > 0 } marked)
        {
            var upgradable = marked
                .Select(p => _upgrades.FirstOrDefault(u => SameId(u.Id, p.Id)) ?? (p.IsUpgradable ? p : null))
                .OfType<WingetPackage>()
                .ToList();
            return (upgradable, "selected in Installed", marked.Count - upgradable.Count);
        }

        return (_upgradesTable.Marked, "ticked in Upgrades", 0);
    }

    internal void UpgradeSelected(bool all = false)
    {
        if (_winget is null)
        {
            return;
        }

        var (packages, from, skipped) = UpgradeTargets(all);
        var targets = packages.Where(p => IsPlainId(p.Id)).ToList();
        if (targets.Count == 0)
        {
            Tell("winget", skipped > 0 ? "None of the selected packages has an upgrade." : "No upgrades selected.");
            return;
        }

        var note = skipped > 0 ? $"\n\n{skipped} selected package(s) have no upgrade and are skipped." : string.Empty;
        if (!Ask("Upgrade packages", $"Upgrade {targets.Count} package(s) {from}?\n\n{PackageList(targets, p => $"{p.Name}  {p.InstalledVersion} → {p.AvailableVersion}")}{note}"))
        {
            return;
        }

        _upgradeLog.Content = string.Empty;
        var includeUnknown = Pickle.Config.Current.Winget.IncludeUnknownVersions;
        Load(
            async ct =>
            {
                var results = new List<WingetOperationResult>();
                foreach (var package in targets)
                {
                    Ui(() => AppendLog(_upgradeLog, $"→ {package.Name} ({package.Id})"));
                    var progress = new UiProgress<WingetProgress>(this, p => AppendLog(_upgradeLog, $"   {p.Stage}{(p.Percent is { } pct ? $" {pct:0}%" : string.Empty)}"));
                    results.Add(await _winget.UpgradeAsync(package.Id, new WingetInstallOptions(IncludeUnknown: includeUnknown), progress, ct).ConfigureAwait(false));
                }

                return results;
            },
            results =>
            {
                foreach (var result in results)
                {
                    AppendResult(_upgradeLog, result);
                }

                Refresh();
            },
            "upgrading…");
    }

    /// <summary>Ticked Installed rows; else the cursor row of the visible package list.</summary>
    internal IReadOnlyList<WingetPackage> UninstallTargets()
    {
        if (_installedTable.Marked is { Count: > 0 } marked)
        {
            return marked;
        }

        var current = ActiveTab == _upgradesTab.Title ? _upgradesTable.Current : _installedTable.Current;
        return current is null ? [] : [current];
    }

    internal void UninstallSelected()
    {
        if (_winget is null)
        {
            return;
        }

        var targets = UninstallTargets().Where(p => IsPlainId(p.Id)).ToList();
        if (targets.Count == 0)
        {
            Tell("winget", "Tick the packages to uninstall in the Installed tab first.");
            return;
        }

        var choice = Choose(
            "Uninstall packages",
            $"Uninstall {targets.Count} package(s)?\n\n{PackageList(targets, p => $"{p.Name}  {p.InstalledVersion}  [{p.Id}]")}\n\n" +
            "Machine-wide packages may need administrator rights (one UAC prompt for all).",
            "_Uninstall",
            "As _administrator",
            "_Cancel");
        if (choice is not (0 or 1))
        {
            return;
        }

        var elevated = choice == 1;
        _installedLog.Content = string.Empty;
        ActiveTab = _installedTab.Title;
        var ids = targets.Select(p => p.Id).ToList();
        Load(
            async ct =>
            {
                var progress = new UiProgress<WingetProgress>(this, p => AppendLog(_installedLog, $"   {p.Stage}{(p.Percent is { } pct ? $" {pct:0}%" : string.Empty)}{(p.Stage is "Elevating" or "Elevated" && p.Message is { } m ? ": " + m : string.Empty)}"));
                if (elevated)
                {
                    Ui(() => AppendLog(_installedLog, $"→ {string.Join(", ", ids)} (administrator)"));
                    return (IReadOnlyList<WingetOperationResult>)[await _winget.UninstallElevatedAsync(ids, progress, ct).ConfigureAwait(false)];
                }

                var results = new List<WingetOperationResult>();
                foreach (var package in targets)
                {
                    Ui(() => AppendLog(_installedLog, $"→ {package.Name} ({package.Id})"));
                    results.Add(await _winget.UninstallAsync(package.Id, progress, ct).ConfigureAwait(false));
                }

                return results;
            },
            results =>
            {
                foreach (var result in results)
                {
                    AppendResult(_installedLog, result);
                }

                if (!elevated && results.Any(r => !r.Success))
                {
                    AppendLog(_installedLog, "Some uninstalls failed. Machine-wide packages may need F8 → As administrator.");
                }

                if (results.All(r => r.Success))
                {
                    _installedTable.Forget(ids);
                }

                Refresh();
            },
            "uninstalling…");
    }

    internal void Search()
    {
        var query = _query.Text.Trim();
        if (_winget is null || query.Length == 0)
        {
            return;
        }

        Load(ct => _winget.SearchAsync(query, ct), ApplyResults, "searching…");
    }

    internal void ApplyResults(IReadOnlyList<WingetPackage> results)
    {
        _results = [.. results];
        _searchTable.Table = new EnumerableTableSource<WingetPackage>(_results, new Dictionary<string, Func<WingetPackage, object>>
        {
            ["Name"] = p => p.Name,
            ["Id"] = p => p.Id,
            ["Version"] = p => p.AvailableVersion ?? string.Empty,
            ["Source"] = p => p.Source ?? string.Empty,
        });
        _searchTable.Update();
        _searchDetails.Content = _results.Count == 0 ? "No results." : "Select a package to see its details.";
        if (_results.Count > 0)
        {
            // Keyboard flow: Enter in the search box, then ↑↓ and Enter (or i) to install.
            if (_query.HasFocus)
            {
                _searchTable.SetFocus();
            }

            ShowDetails(0);
        }
    }

    internal void ShowDetails(int row)
    {
        if (_winget is null || row < 0 || row >= _results.Count || !IsPlainId(_results[row].Id))
        {
            return;
        }

        var id = _results[row].Id;
        Load(ct => _winget.GetDetailsAsync(id, ct), ApplyDetails, "details…");
    }

    internal void ApplyDetails(WingetPackageDetails? details)
    {
        if (details is not null && SelectedResult() is { } selected && !SameId(selected.Id, details.Id))
        {
            return;
        }

        _details = details;
        if (details is null)
        {
            _searchDetails.Content = "No details available.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{details.Name} [{details.Id}]");
        sb.AppendLine($"Publisher: {details.Publisher ?? "—"}");
        sb.AppendLine($"Latest:    {details.LatestVersion ?? "—"}");
        sb.AppendLine($"License:   {details.License ?? "—"}");
        sb.AppendLine($"Homepage:  {details.Homepage ?? "—"}");
        if (details.AvailableVersions.Count > 0)
        {
            sb.AppendLine($"Versions:  {string.Join(", ", details.AvailableVersions.Take(10))}{(details.AvailableVersions.Count > 10 ? ", …" : string.Empty)}");
        }

        if (details.Description is { } description)
        {
            sb.AppendLine().AppendLine(description);
        }

        _searchDetails.Content = sb.ToString();
        _version.Text = details.LatestVersion ?? string.Empty;
    }

    /// <summary>Installs the highlighted search result (Enter, i, or the Install button).</summary>
    internal void InstallSelected()
    {
        if (_winget is null || SelectedResult() is not { } package || !IsPlainId(package.Id))
        {
            Tell("winget", "Search for a package and select it first.");
            return;
        }

        var id = package.Id;
        var details = _details is { } d && SameId(d.Id, id) ? d : null;
        var typed = _version.Text.Trim();
        var version = details is null || typed.Length == 0 || typed == details.LatestVersion ? null : typed;
        var scope = Scope;
        var note = scope == WingetScope.Machine ? "\n\nMachine-wide installs need administrator rights (UAC prompt)." : string.Empty;
        if (!Ask("Install package", $"Install {package.Name} [{id}]{(version is null ? string.Empty : " " + version)} ({scope.ToString().ToLowerInvariant()} scope)?{note}"))
        {
            return;
        }

        var progress = new UiProgress<WingetProgress>(this, p => _searchDetails.Content = $"{p.Stage}{(p.Percent is { } pct ? $" {pct:0}%" : string.Empty)}\n{p.Message}");
        Load(
            ct => _winget.InstallAsync(id, new WingetInstallOptions(version, scope), progress, ct),
            result =>
            {
                _searchDetails.Content = (result.Success ? "✓ " : "✗ ") + result.Message + (result.Success || string.IsNullOrWhiteSpace(result.Output) ? string.Empty : "\n\n" + result.Output);
                Refresh();
            },
            "installing…");
    }

    /// <summary>
    /// Re-registers the winget source. Current user by default (no UAC; Add-AppxPackage registers per user), or for
    /// the administrator account elevated sessions run as.
    /// </summary>
    internal void RepairSource(bool elevated = false)
    {
        if (_winget is null)
        {
            return;
        }

        var message = elevated
            ? "Re-register the winget source package for the administrator account?\n\n" +
              "Only needed when elevated shells run as a different account. This runs\n" +
              "Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix'\n" +
              "in an elevated helper; Windows will show a UAC prompt."
            : "Re-register the winget source package for your account?\n\n" +
              "This fixes 'failed when searching source' errors by running\n" +
              "Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix'\n" +
              "(no administrator rights needed).";
        if (!Ask("Repair winget source", message))
        {
            return;
        }

        _repairOutput.Content = elevated ? "Waiting for the administrator (UAC) prompt…" : "Re-registering the winget source…";
        Load(
            ct => _winget.RepairSourceAsync(elevated, ct),
            result =>
            {
                _repairOutput.Content = (result.Success ? "✓ " : "✗ ") + result.Message + (string.IsNullOrWhiteSpace(result.Output) ? string.Empty : "\n\n" + result.Output);
                if (result.Success)
                {
                    Refresh();
                }
            },
            "repairing source…");
    }

    internal void InstallModule() => InstallModule(askFirst: true);

    private void InstallModule(bool askFirst)
    {
        if (_winget is null)
        {
            return;
        }

        if (askFirst && !Ask("Install Microsoft.WinGet.Client", "Install the Microsoft.WinGet.Client PowerShell module from the PowerShell Gallery for the current user?\n\nIt gives Pickle faster, structured winget results."))
        {
            return;
        }

        Load(
            _winget.InstallClientModuleAsync,
            result =>
            {
                Tell("winget", result.Message);
                Refresh();
            },
            "installing module…");
    }

    private static bool IsPlainId(string id) =>
        id.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string PackageList(IReadOnlyList<WingetPackage> packages, Func<WingetPackage, string> line) =>
        string.Join("\n", packages.Take(12).Select(line)) + (packages.Count > 12 ? $"\n… and {packages.Count - 12} more" : string.Empty);

    private static void AppendLog(TextPane pane, string line) =>
        pane.Content = string.IsNullOrEmpty(pane.Content) ? line : pane.Content + "\n" + line;

    private static void AppendResult(TextPane pane, WingetOperationResult result)
    {
        AppendLog(pane, (result.Success ? "✓ " : "✗ ") + result.Message);
        if (!result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            AppendLog(pane, string.Join("\n", result.Output.Split('\n').TakeLast(20).Select(l => "   │ " + l.TrimEnd())));
        }
    }

    private WingetPackage? SelectedResult()
    {
        var row = SelectedRow(_searchTable);
        return row >= 0 && row < _results.Count ? _results[row] : null;
    }

    private View BuildInstalledTab()
    {
        _filter.ValueChanged += (_, _) => ApplyFilter();
        var uninstall = MakeButton("_Uninstall", UninstallSelected);
        uninstall.Y = Pos.AnchorEnd(6);
        var upgrade = MakeButton("Up_grade", () => UpgradeSelected());
        upgrade.X = Pos.Right(uninstall) + 1;
        upgrade.Y = Pos.AnchorEnd(6);
        var help = new Label { X = Pos.Right(upgrade) + 2, Y = Pos.AnchorEnd(6), Text = "ticked packages, or the highlighted one" };
        _installedLog.Y = Pos.AnchorEnd(5);
        _installedLog.Height = 5;
        _installedTab.Add(new Label { Text = "Filter:", X = 0, Y = 0 }, _filter, _installedTable, uninstall, upgrade, help, _installedLog);
        return _installedTab;
    }

    private View BuildUpgradesTab()
    {
        var selected = MakeButton("_Upgrade selected", () => UpgradeSelected());
        var all = MakeButton("Upgrade _all", () => UpgradeSelected(all: true));
        all.X = Pos.Right(selected) + 1;
        _upgradeLog.Y = Pos.AnchorEnd(6);
        _upgradeLog.Height = 6;
        _upgradesTab.Add(selected, all, _upgradesTable, _upgradeLog);
        return _upgradesTab;
    }

    private View BuildSearchTab()
    {
        var tab = new View { Title = "Search", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _query.Accepting += (_, e) =>
        {
            Search();
            e.Handled = true;
        };
        var go = MakeButton("_Find", Search);
        go.X = Pos.Right(_query) + 1;
        var help = new Label { X = 0, Y = 1, Text = "Enter search · ↑↓ details · Enter/i install · / new search" };
        _searchTable.ValueChanged += (_, _) => ShowDetails(SelectedRow(_searchTable));
        _searchTable.Accepting += (_, e) =>
        {
            InstallSelected();
            e.Handled = true;
        };
        _searchTable.KeyDown += (_, key) =>
        {
            if (key == Key.I || key == Key.I.WithShift)
            {
                InstallSelected();
                key.Handled = true;
            }
            else if (key == new Key('/'))
            {
                _query.SetFocus();
                key.Handled = true;
            }
        };
        var right = new View { X = Pos.Right(_searchTable), Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        var versionLabel = new Label { Text = "Version:", Y = 0 };
        _version.X = Pos.Right(versionLabel) + 1;
        _scope.Y = 1;
        var install = MakeButton("_Install (i)", InstallSelected);
        install.Y = 2;
        _searchDetails.Y = 3;
        _searchDetails.Height = Dim.Fill();
        right.Add(versionLabel, _version, _scope, install, _searchDetails);
        tab.Add(new Label { Text = "Search:", X = 0, Y = 0 }, _query, go, help, _searchTable, right);
        return tab;
    }

    private View BuildSourcesTab()
    {
        var tab = new View { Title = "Sources", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        var repair = MakeButton("_Repair source", () => RepairSource());
        var admin = MakeButton("Repair as _admin", () => RepairSource(elevated: true));
        admin.X = Pos.Right(repair) + 1;
        var note = new Label { X = Pos.Right(admin) + 2, Text = "fixes source errors" };
        _repairOutput.Y = Pos.Bottom(_sourcesTable);
        _repairOutput.Height = Dim.Fill();
        _repairOutput.Content = "Repair re-registers source.msix for your account; the full output appears here.";
        tab.Add(repair, admin, note, _sourcesTable, _repairOutput);
        return tab;
    }
}
