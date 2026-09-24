using System.Text;
using System.Text.RegularExpressions;
using Pickle.Abstractions.Services;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>Security/critical first, then other, drivers and optional (same order as <c>pk update check</c>).</summary>
internal static class UpdateGrouping
{
    public const int SecurityCritical = 0;
    public const int Other = 1;
    public const int Drivers = 2;
    public const int Optional = 3;

    public static int Of(WindowsUpdateInfo update)
    {
        if (update.IsOptional)
        {
            return Optional;
        }

        if (update.IsDriver)
        {
            return Drivers;
        }

        return update.Severity is not null || update.Categories.Any(c => c.Contains("Security", StringComparison.OrdinalIgnoreCase) || c.Contains("Critical", StringComparison.OrdinalIgnoreCase))
            ? SecurityCritical
            : Other;
    }

    public static string Title(int group) => group switch
    {
        SecurityCritical => "Security & critical",
        Drivers => "Drivers",
        Optional => "Optional",
        _ => "Other",
    };

    public static string Short(int group) => group switch
    {
        SecurityCritical => "Security",
        Drivers => "Driver",
        Optional => "Optional",
        _ => "Other",
    };
}

/// <summary>
/// Available Windows updates: status header, Check (background search with progress), a ticked list grouped by kind
/// (same selection keys and mouse gestures as the winget lists), a details pane, Install selected and Install KB….
/// Used by the updates panel and the winget panel's "Windows Updates" tab.
/// </summary>
public sealed partial class UpdatesView : View
{
    private readonly WindowsPanelBase _owner;
    private readonly IWindowsUpdateService _service;
    private readonly Label _status;
    private readonly CheckBox _drivers;
    private readonly CheckBox _optional;
    private readonly SelectionTable<WindowsUpdateInfo> _table;
    private readonly TextPane _details;
    private readonly TextPane _log;
    private List<WindowsUpdateInfo> _rows = [];

    public UpdatesView(WindowsPanelBase owner, IWindowsUpdateService service)
    {
        _owner = owner;
        _service = service;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        _status = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Status: loading…" };
        var check = WindowsPanelBase.MakeButton("_Check", Check);
        check.Y = 1;
        _drivers = new CheckBox { Text = "_Drivers", X = Pos.Right(check) + 1, Y = 1, Value = CheckState.Checked };
        _optional = new CheckBox { Text = "_Optional", X = Pos.Right(_drivers) + 1, Y = 1 };
        var install = WindowsPanelBase.MakeButton("_Install selected", InstallSelected);
        install.X = Pos.Right(_optional) + 2;
        install.Y = 1;
        var kb = WindowsPanelBase.MakeButton("Install _KB…", () => InstallKb());
        kb.X = Pos.Right(install) + 1;
        kb.Y = 1;

        _table = new SelectionTable<WindowsUpdateInfo>(
            u => u.UpdateId,
            u => UpdateGrouping.Of(u) <= UpdateGrouping.Other,
            ("Kind", u => UpdateGrouping.Short(UpdateGrouping.Of(u))),
            ("KB", u => u.KbArticle),
            ("Title", u => u.Title),
            ("Size", u => WindowsPanelBase.Size(u.SizeBytes)))
        {
            X = 0,
            Y = 3,
            Width = Dim.Percent(60),
            Height = Dim.Fill(6),
        };
        _table.ValueChanged += (_, _) => ShowDetails();
        _details = WindowsPanelBase.MakeText("Details");
        _details.X = Pos.Right(_table);
        _details.Y = 3;
        _details.Height = Dim.Fill(6);
        _log = WindowsPanelBase.MakeText("Progress · Space tick · Shift+click range · Ctrl+A all");
        _log.Y = Pos.AnchorEnd(6);
        _log.Height = 6;
        Add(_status, check, _drivers, _optional, install, kb, _table, _details, _log);
        SetRows([]);
    }

    /// <summary>The listed updates in display order (security/critical first).</summary>
    internal IReadOnlyList<WindowsUpdateInfo> Rows => _rows;

    internal SelectionTable<WindowsUpdateInfo> Table => _table;

    internal string StatusText => _status.Text;

    internal string LogText => _log.Content;

    internal bool IsSelected(WindowsUpdateInfo update) => _table.IsMarked(update);

    /// <summary>Loads the status header (does not search; searching can take minutes).</summary>
    public void Start() => _owner.Load(_service.GetStatusAsync, ApplyStatus, "status…");

    public void Check()
    {
        var query = new WindowsUpdateQuery(_drivers.Value == CheckState.Checked, _optional.Value == CheckState.Checked);
        _log.Content = "Searching Windows Update (the first search after a restart can take a few minutes)…";
        var progress = new UiProgress<WindowsUpdateProgress>(_owner, p => _log.Content = Describe(p));
        _owner.Load(
            ct => _service.SearchAsync(query, progress, ct),
            updates =>
            {
                SetRows(updates);
                _log.Content = updates.Count == 0 ? "No updates available." : $"{updates.Count} update(s) available.";
            },
            "searching…");
    }

    internal void ApplyStatus(WindowsUpdateStatus status)
    {
        if (!status.IsSupported)
        {
            _status.Text = "Windows Update is only available on Windows.";
            return;
        }

        var parts = new List<string>
        {
            status.IsManagedByOrganization ? "Managed by your organization: " + status.ManagedReason : "Not managed",
            status.RebootRequired ? "RESTART REQUIRED" : "no restart pending",
            "last check " + WindowsPanelBase.When(status.LastSearchSuccess),
            "last install " + WindowsPanelBase.When(status.LastInstallSuccess),
        };
        _status.Text = string.Join("  ·  ", parts);
    }

    internal void SetRows(IReadOnlyList<WindowsUpdateInfo> updates)
    {
        _rows = [.. updates.OrderBy(UpdateGrouping.Of).ThenBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase)];
        _table.SetItems(_rows);
        ShowDetails();
    }

    internal void InstallSelected()
    {
        var selected = _table.Marked.ToList();
        if (selected.Count == 0)
        {
            _owner.Tell("Windows Update", "Nothing selected. Press Check, then tick the updates to install (or use Install KB…).");
            return;
        }

        Install(selected);
    }

    /// <summary>Install one update by KB number: asks for it when <paramref name="kb"/> is null.</summary>
    internal void InstallKb(string? kb = null)
    {
        kb ??= _owner.AskText("Install a KB", "KB number (e.g. KB5031455):");
        if (kb is null)
        {
            return;
        }

        if (KbRegex().Match(kb.Trim()) is not { Success: true } match)
        {
            _owner.Tell("Windows Update", $"'{kb.Trim()}' is not a KB number (e.g. KB5031455).");
            return;
        }

        var wanted = "KB" + match.Groups[1].Value;
        var listed = _rows.Where(u => SameKb(u, wanted)).ToList();
        if (listed.Count > 0)
        {
            Install(listed);
            return;
        }

        // Not in the list (not searched yet, or a driver/optional update that wasn't included): look everywhere.
        _log.Content = $"Looking for {wanted}…";
        var progress = new UiProgress<WindowsUpdateProgress>(_owner, p => _log.Content = $"Looking for {wanted}: {Describe(p)}");
        _owner.Load(
            ct => _service.SearchAsync(new WindowsUpdateQuery(IncludeDrivers: true, IncludeOptional: true), progress, ct),
            updates =>
            {
                var found = updates.Where(u => SameKb(u, wanted)).ToList();
                if (found.Count == 0)
                {
                    _log.Content = $"{wanted} is not offered for this PC.";
                    _owner.Tell("Windows Update", $"{wanted} is not offered for this PC: it is already installed, superseded by a newer update, or not applicable.");
                    return;
                }

                Install(found);
            },
            "searching…");
    }

    private void Install(IReadOnlyList<WindowsUpdateInfo> updates)
    {
        var list = string.Join("\n", updates.Take(10).Select(u => $"{u.KbArticle ?? "—",-10} {u.Title}")) + (updates.Count > 10 ? $"\n… and {updates.Count - 10} more" : string.Empty);
        var message = $"Install {updates.Count} update(s)?\n\n{list}\n\nWindows will ask for administrator permission (UAC) unless Pickle is already elevated.";
        if (!_owner.Ask("Install updates", message))
        {
            return;
        }

        foreach (var update in updates)
        {
            _table.SetMarked(update, true);
        }

        _log.Content = string.Empty;
        var progress = new UiProgress<WindowsUpdateProgress>(_owner, p => AppendLog(Describe(p)));
        _owner.Load(
            ct => _service.InstallAsync([.. updates.Select(u => u.UpdateId)], progress, ct),
            result =>
            {
                foreach (var (_, title, ok, error) in result.Results)
                {
                    AppendLog((ok ? "✓ " : "✗ ") + title + (error is null ? string.Empty : ": " + error));
                }

                AppendLog(result.Message + (result.RebootRequired ? " Restart your PC to finish." : string.Empty));
                Start();
            },
            "installing…");
    }

    private static bool SameKb(WindowsUpdateInfo update, string kb) =>
        string.Equals(update.KbArticle, kb, StringComparison.OrdinalIgnoreCase) || update.Title.Contains(kb, StringComparison.OrdinalIgnoreCase);

    private static string Describe(WindowsUpdateProgress p) =>
        string.Join(' ', new[] { p.Stage, p.CurrentUpdate, p.Percent is { } pct ? $"{pct:0}%" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));

    [GeneratedRegex(@"^(?:KB)?(\d{4,8})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KbRegex();

    private void AppendLog(string line) => _log.Content = string.IsNullOrEmpty(_log.Content) ? line : _log.Content + "\n" + line;

    private void ShowDetails()
    {
        if (_table.Current is not { } u)
        {
            _details.Content = _rows.Count == 0 ? "Press Check to search for updates." : string.Empty;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(u.Title);
        sb.AppendLine();
        sb.AppendLine($"Kind:      {UpdateGrouping.Title(UpdateGrouping.Of(u))}");
        sb.AppendLine($"KB:        {u.KbArticle ?? "—"}");
        sb.AppendLine($"Severity:  {u.Severity ?? "—"}");
        sb.AppendLine($"Size:      {WindowsPanelBase.Size(u.SizeBytes)}");
        sb.AppendLine($"Released:  {WindowsPanelBase.When(u.ReleaseDate)}");
        sb.AppendLine($"Downloaded: {(u.IsDownloaded ? "yes" : "no")}  Mandatory: {(u.IsMandatory ? "yes" : "no")}  Restart: {(u.RebootMayBeRequired ? "may be required" : "no")}");
        sb.AppendLine($"Categories: {string.Join(", ", u.Categories)}");
        if (u.Description is { } d)
        {
            sb.AppendLine().AppendLine(d);
        }

        _details.Content = sb.ToString();
    }
}

/// <summary>Windows Update history as a table.</summary>
public sealed class UpdatesHistoryView : View
{
    private readonly WindowsPanelBase _owner;
    private readonly IWindowsUpdateService _service;
    private readonly TableView _table;

    public UpdatesHistoryView(WindowsPanelBase owner, IWindowsUpdateService service)
    {
        _owner = owner;
        _service = service;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        _table = new TableView { Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        Add(_table);
        Apply([]);
    }

    internal int Count { get; private set; }

    public void Start() => _owner.Load(ct => _service.GetHistoryAsync(100, ct), Apply, "history…");

    internal void Apply(IReadOnlyList<WindowsUpdateHistoryEntry> entries)
    {
        Count = entries.Count;
        _table.Table = new EnumerableTableSource<WindowsUpdateHistoryEntry>(entries, new Dictionary<string, Func<WindowsUpdateHistoryEntry, object>>
        {
            ["Date"] = e => WindowsPanelBase.When(e.Date),
            ["Result"] = e => e.Result,
            ["Operation"] = e => e.Operation,
            ["KB"] = e => e.KbArticle ?? string.Empty,
            ["Title"] = e => e.Title,
        });
        _table.Update();
    }
}

/// <summary>Progress reports marshalled to the UI thread in order.</summary>
internal sealed class UiProgress<T>(WindowsPanelBase owner, Action<T> report) : IProgress<T>
{
    public void Report(T value) => owner.Ui(() => report(value));
}
