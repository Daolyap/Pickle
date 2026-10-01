using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Input;

namespace Pickle.Tui.Panels.Admin;

/// <summary>
/// Packages of the system's package manager (Alt+K): installed, upgradable, or search results. Install, remove and upgrade
/// close the panel and run the manager's own command in the shell (with sudo where needed), so you see its progress and
/// confirm prompts, exactly as when typing it.
/// </summary>
internal sealed class PackagesPanel : ResourcePanel<SystemPackage>
{
    public const string PanelId = "packages";

    private enum View
    {
        Installed,
        Upgradable,
        Search,
    }

    private readonly ISystemPackageManager _manager;
    private View _view = View.Installed;
    private string _query = string.Empty;

    public static PanelDescriptor Descriptor { get; } = new()
    {
        Id = PanelId,
        Title = "Packages",
        Description = "apt, dnf, pacman, zypper or Homebrew: installed, upgradable and search; install, remove, upgrade",
        DefaultKey = "Alt+K",
        CreateView = context => new PackagesPanel(context),
    };

    public PackagesPanel(PanelContext context)
        : base(context, "Packages", p => p.Name, "Package")
    {
        _manager = context.Pickle.Services.Get<ISystemPackageManager>() is { IsSupported: true } manager
            ? manager
            : throw new InvalidOperationException("No supported package manager (apt, dnf, pacman, zypper or Homebrew) was found. On Windows use the winget panel (Alt+W).");
        if (!string.IsNullOrEmpty(context.Argument))
        {
            _query = context.Argument;
            _view = View.Search;
        }

        Retitle();
        AddHint(Key.F2, "Search", AskSearch);
        AddCommand(Key.F3, "Install", p => p.Installed ? null : _manager.ShellCommand(PackageAction.Install, [p.Name]));
        AddCommand(Key.F4, "Remove", p => p.Installed ? _manager.ShellCommand(PackageAction.Remove, [p.Name]) : null);
        AddCommand(Key.F7, "Upgrade", p => p.Upgradable ? _manager.ShellCommand(PackageAction.Upgrade, [p.Name]) : null);
        AddHint(Key.F6, "Installed/Upgrades", CycleView);
        AddHint(Key.F8, "Upgrade all", () => Complete(new PanelResult(PanelResultKind.RunCommand, _manager.ShellCommand(PackageAction.UpgradeAll, []))));
        AddHint(Key.F9, "Refresh index", () => Complete(new PanelResult(PanelResultKind.RunCommand, _manager.ShellCommand(PackageAction.RefreshIndex, []))));
    }

    protected override string EmptyMessage => _view switch
    {
        View.Upgradable => "Everything is up to date (F9 refreshes the package index first).",
        View.Search => string.IsNullOrEmpty(_query) ? "F2 searches the repositories." : $"Nothing matches '{_query}'.",
        _ => "No packages found.",
    };

    protected override Task<IReadOnlyList<SystemPackage>> LoadAsync(CancellationToken cancellationToken) => _view switch
    {
        View.Upgradable => _manager.ListUpgradesAsync(cancellationToken),
        View.Search when !string.IsNullOrWhiteSpace(_query) => _manager.SearchAsync(_query, cancellationToken),
        View.Search => Task.FromResult<IReadOnlyList<SystemPackage>>([]),
        _ => _manager.ListInstalledAsync(cancellationToken),
    };

    protected override Task<IReadOnlyList<string>> DescribeAsync(SystemPackage item, CancellationToken cancellationToken) =>
        _manager.InfoAsync(item.Name, cancellationToken);

    protected override string KeyOf(SystemPackage item) => item.Name;

    protected override string? Hint(SystemPackage item) =>
        item.Upgradable ? $"{item.Version} → {item.NewVersion}" : item.Installed ? item.Version : "not installed";

    protected override string? Detail(SystemPackage item) => item.Description;

    protected override Terminal.Gui.Drawing.Color? ItemColor(SystemPackage item) =>
        item.Upgradable ? Schemes.Warning.Foreground : item.Installed ? null : Schemes.Muted.Foreground;

    private void Retitle() =>
        PanelTitle = $"Packages ({_manager.Name}) · {(_view == View.Search ? $"search '{_query}'" : _view == View.Upgradable ? "upgradable" : "installed")}";

    private void CycleView()
    {
        _view = _view == View.Installed ? View.Upgradable : View.Installed;
        Retitle();
        Reload();
    }

    private void AskSearch()
    {
        if (Prompt("Search packages", "name or keyword", _query) is { Length: > 0 } query)
        {
            _query = query.Trim();
            _view = View.Search;
            Retitle();
            Reload();
        }
    }
}
