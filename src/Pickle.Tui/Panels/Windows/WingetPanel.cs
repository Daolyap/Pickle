using System.Text;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

internal sealed class UpgradeRow(WingetPackage package)
{
    public WingetPackage Package { get; } = package;

    public bool Selected { get; set; } = true;
}

/// <summary>
/// winget dashboard (Alt+W): Installed (filterable, upgradable highlighted), Upgrades (multi-select), Search (details,
/// install with version and scope), Sources (repair) and Windows Updates.
/// </summary>
public sealed class WingetPanel : WindowsPanelBase
{
    private static readonly string[] ScopeLabels = ["Any", "User", "Machine"];

    private readonly IWingetService? _winget;
    private readonly Label _backend;
    private readonly Button _installModule;
    private readonly TextField _filter = new() { X = 8, Y = 0, Width = Dim.Fill() };
    private readonly TableView _installedTable = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
    private readonly TableView _upgradesTable = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(8), FullRowSelect = true };
    private readonly TextPane _upgradeLog = MakeText("Progress");
    private readonly TextField _query = new() { X = 8, Y = 0, Width = Dim.Fill(12) };
    private readonly TableView _searchTable = new() { Y = 1, Width = Dim.Percent(55), Height = Dim.Fill(), FullRowSelect = true };
    private readonly TextPane _searchDetails = MakeText("Details");
    private readonly TextField _version = new() { Width = 18 };
    private readonly OptionSelector _scope = new() { Labels = ScopeLabels, Orientation = Orientation.Horizontal, Value = 0 };
    private readonly TableView _sourcesTable = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
    private readonly UpdatesView? _updates;
    private List<WingetPackage> _installed = [];
    private List<UpgradeRow> _upgrades = [];
    private List<WingetPackage> _results = [];
    private WingetPackageDetails? _details;

    public WingetPanel(PanelContext context)
        : base(context, "winget")
    {
        _winget = Service<IWingetService>();
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

        var tabs = new Tabs { Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        tabs.Add(BuildInstalledTab(), BuildUpgradesTab(), BuildSearchTab(), BuildSourcesTab());
        if (Service<IWindowsUpdateService>() is { IsSupported: true } wu)
        {
            _updates = new UpdatesView(this, wu) { Title = "Windows Updates" };
            tabs.Add(_updates);
        }

        Body.Add(_backend, _installModule, tabs);
        AddHint(Key.F5, "Refresh", Refresh);
        AddHint(Key.F9, "Upgrade selected", () => UpgradeSelected());
    }

    internal IReadOnlyList<WingetPackage> InstalledRows => _installed;

    internal IReadOnlyList<UpgradeRow> UpgradeRows => _upgrades;

    internal IReadOnlyList<WingetPackage> SearchResults => _results;

    internal string BackendText => _backend.Text;

    internal bool InstallModuleVisible => _installModule.Visible;

    internal string UpgradeLog => _upgradeLog.Content;

    internal string DetailsText => _searchDetails.Content;

    internal TableView InstalledTable => _installedTable;

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
        _upgrades = [.. packages.Select(p => new UpgradeRow(p))];
        var source = new EnumerableTableSource<UpgradeRow>(_upgrades, new Dictionary<string, Func<UpgradeRow, object>>
        {
            ["Name"] = r => r.Package.Name,
            ["Id"] = r => r.Package.Id,
            ["Installed"] = r => r.Package.InstalledVersion ?? string.Empty,
            ["Available"] = r => r.Package.AvailableVersion ?? string.Empty,
        });
        _upgradesTable.Table = new CheckBoxTableSourceWrapperByObject<UpgradeRow>(_upgradesTable, source, r => r.Selected, (r, v) => r.Selected = v);
        _upgradesTable.Update();
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
        var rows = filter.Length == 0
            ? _installed
            : [.. _installed.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        _installedTable.Table = new EnumerableTableSource<WingetPackage>(rows, new Dictionary<string, Func<WingetPackage, object>>
        {
            ["Name"] = p => p.Name,
            ["Id"] = p => p.Id,
            ["Version"] = p => p.InstalledVersion ?? string.Empty,
            ["Available"] = p => p.AvailableVersion ?? string.Empty,
            ["Source"] = p => p.Source ?? string.Empty,
        });
        var highlight = HighlightScheme();
        _installedTable.Style.RowColorGetter = args => args.RowIndex < rows.Count && rows[args.RowIndex].IsUpgradable ? highlight : null;
        _installedTable.Update();
        FilteredCount = rows.Count;
    }

    internal int FilteredCount { get; private set; }

    internal void UpgradeSelected(bool all = false)
    {
        if (_winget is null)
        {
            return;
        }

        var targets = _upgrades.Where(r => all || r.Selected).Select(r => r.Package).Where(p => IsPlainId(p.Id)).ToList();
        if (targets.Count == 0)
        {
            Tell("winget", "No upgrades selected.");
            return;
        }

        if (!Ask("Upgrade packages", $"Upgrade {targets.Count} package(s)?\n\n{string.Join("\n", targets.Take(12).Select(p => $"{p.Name}  {p.InstalledVersion} → {p.AvailableVersion}"))}{(targets.Count > 12 ? "\n…" : string.Empty)}"))
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
                    Ui(() => AppendUpgradeLog($"→ {package.Name} ({package.Id})"));
                    var progress = new UiProgress<WingetProgress>(this, p => AppendUpgradeLog($"   {p.Stage}{(p.Percent is { } pct ? $" {pct:0}%" : string.Empty)}"));
                    results.Add(await _winget.UpgradeAsync(package.Id, new WingetInstallOptions(IncludeUnknown: includeUnknown), progress, ct).ConfigureAwait(false));
                }

                return results;
            },
            results =>
            {
                foreach (var result in results)
                {
                    AppendUpgradeLog((result.Success ? "✓ " : "✗ ") + result.Message);
                }

                Refresh();
            },
            "upgrading…");
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

    internal void InstallSelected()
    {
        if (_winget is null || _details is null)
        {
            Tell("winget", "Search for a package and select it first.");
            return;
        }

        var id = _details.Id;
        var version = string.IsNullOrWhiteSpace(_version.Text) || _version.Text == _details.LatestVersion ? null : _version.Text.Trim();
        var scope = Scope;
        var note = scope == WingetScope.Machine ? "\n\nMachine-wide installs need administrator rights (UAC prompt)." : string.Empty;
        if (!Ask("Install package", $"Install {_details.Name} [{id}]{(version is null ? string.Empty : " " + version)} ({scope.ToString().ToLowerInvariant()} scope)?{note}"))
        {
            return;
        }

        var progress = new UiProgress<WingetProgress>(this, p => _searchDetails.Content = $"{p.Stage}{(p.Percent is { } pct ? $" {pct:0}%" : string.Empty)}\n{p.Message}");
        Load(
            ct => _winget.InstallAsync(id, new WingetInstallOptions(version, scope), progress, ct),
            result =>
            {
                _searchDetails.Content = (result.Success ? "✓ " : "✗ ") + result.Message;
                Refresh();
            },
            "installing…");
    }

    internal void RepairSource()
    {
        if (_winget is null)
        {
            return;
        }

        const string message =
            "Re-register the winget source package for administrator sessions?\n\n" +
            "This fixes 'failed when searching source' errors in elevated shells by running\n" +
            "Add-AppxPackage -Path 'https://cdn.winget.microsoft.com/cache/source.msix'\n" +
            "in an elevated helper. Windows will show a UAC prompt.";
        if (!Ask("Repair winget source", message))
        {
            return;
        }

        Load(ct => _winget.RepairSourceAsync(true, ct), result => Tell("winget", result.Message), "repairing source…");
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

    private void AppendUpgradeLog(string line) =>
        _upgradeLog.Content = string.IsNullOrEmpty(_upgradeLog.Content) ? line : _upgradeLog.Content + "\n" + line;

    private View BuildInstalledTab()
    {
        var tab = new View { Title = "Installed", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _filter.ValueChanged += (_, _) => ApplyFilter();
        tab.Add(new Label { Text = "Filter:", X = 0, Y = 0 }, _filter, _installedTable);
        return tab;
    }

    private View BuildUpgradesTab()
    {
        var tab = new View { Title = "Upgrades", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        var selected = MakeButton("_Upgrade selected", () => UpgradeSelected());
        var all = MakeButton("Upgrade _all", () => UpgradeSelected(all: true));
        all.X = Pos.Right(selected) + 1;
        _upgradeLog.Y = Pos.AnchorEnd(8);
        _upgradeLog.Height = 8;
        tab.Add(selected, all, _upgradesTable, _upgradeLog);
        return tab;
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
        _searchTable.ValueChanged += (_, _) => ShowDetails(SelectedRow(_searchTable));
        var right = new View { X = Pos.Right(_searchTable), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        var versionLabel = new Label { Text = "Version:", Y = 0 };
        _version.X = Pos.Right(versionLabel) + 1;
        _scope.Y = 1;
        var install = MakeButton("_Install", InstallSelected);
        install.Y = 2;
        _searchDetails.Y = 3;
        _searchDetails.Height = Dim.Fill();
        right.Add(versionLabel, _version, _scope, install, _searchDetails);
        tab.Add(new Label { Text = "Search:", X = 0, Y = 0 }, _query, go, _searchTable, right);
        return tab;
    }

    private View BuildSourcesTab()
    {
        var tab = new View { Title = "Sources", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        var repair = MakeButton("_Repair source (admin)", RepairSource);
        var note = new Label { X = Pos.Right(repair) + 2, Text = "fixes winget source errors in elevated sessions" };
        tab.Add(repair, note, _sourcesTable);
        return tab;
    }
}
