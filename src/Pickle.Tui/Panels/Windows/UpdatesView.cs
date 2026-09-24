using System.Text;
using Pickle.Abstractions.Services;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.Windows;

/// <summary>A selectable row in the updates list.</summary>
internal sealed class UpdateRow(WindowsUpdateInfo update)
{
    public WindowsUpdateInfo Update { get; } = update;

    public int Group { get; } = UpdateGrouping.Of(update);

    public bool Selected { get; set; } = UpdateGrouping.Of(update) <= UpdateGrouping.Other;
}

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
}

/// <summary>
/// Available Windows updates: status header, Check (background search), a checkbox list grouped by kind, a details
/// pane and Install selected. Used by the updates panel and the winget panel's "Windows Updates" tab.
/// </summary>
public sealed class UpdatesView : View
{
    private readonly WindowsPanelBase _owner;
    private readonly IWindowsUpdateService _service;
    private readonly Label _status;
    private readonly CheckBox _drivers;
    private readonly CheckBox _optional;
    private readonly TableView _table;
    private readonly TextPane _details;
    private readonly TextPane _log;
    private List<UpdateRow> _rows = [];

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

        _table = new TableView { X = 0, Y = 3, Width = Dim.Percent(60), Height = Dim.Fill(6), FullRowSelect = true };
        _table.ValueChanged += (_, _) => ShowDetails();
        _details = WindowsPanelBase.MakeText("Details");
        _details.X = Pos.Right(_table);
        _details.Y = 3;
        _details.Height = Dim.Fill(6);
        _log = WindowsPanelBase.MakeText("Progress");
        _log.Y = Pos.AnchorEnd(6);
        _log.Height = 6;
        Add(_status, check, _drivers, _optional, install, _table, _details, _log);
        SetRows([]);
    }

    internal IReadOnlyList<UpdateRow> Rows => _rows;

    internal TableView Table => _table;

    internal string StatusText => _status.Text;

    internal string LogText => _log.Content;

    /// <summary>Loads the status header (does not search; searching can take minutes).</summary>
    public void Start() => _owner.Load(_service.GetStatusAsync, ApplyStatus, "status…");

    public void Check() => _owner.Load(
        ct => _service.SearchAsync(new WindowsUpdateQuery(_drivers.Value == CheckState.Checked, _optional.Value == CheckState.Checked), ct),
        updates => SetRows(updates),
        "searching…");

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
        _rows = [.. updates.Select(u => new UpdateRow(u)).OrderBy(r => r.Group).ThenBy(r => r.Update.Title, StringComparer.CurrentCultureIgnoreCase)];
        var source = new EnumerableTableSource<UpdateRow>(_rows, new Dictionary<string, Func<UpdateRow, object>>
        {
            ["Group"] = r => UpdateGrouping.Title(r.Group),
            ["KB"] = r => r.Update.KbArticle ?? string.Empty,
            ["Title"] = r => r.Update.Title,
            ["Size"] = r => WindowsPanelBase.Size(r.Update.SizeBytes),
        });
        _table.Table = new CheckBoxTableSourceWrapperByObject<UpdateRow>(_table, source, r => r.Selected, (r, v) => r.Selected = v);
        _table.Update();
        ShowDetails();
    }

    internal void InstallSelected()
    {
        var selected = _rows.Where(r => r.Selected).Select(r => r.Update).ToList();
        if (selected.Count == 0)
        {
            _owner.Tell("Windows Update", "Nothing selected. Press Check, then tick the updates to install.");
            return;
        }

        var message = $"Install {selected.Count} update(s)?\n\nWindows will ask for administrator permission (UAC) unless Pickle is already elevated.";
        if (!_owner.Ask("Install updates", message))
        {
            return;
        }

        _log.Content = string.Empty;
        var progress = new UiProgress<WindowsUpdateProgress>(_owner, p => AppendLog($"{p.Stage} {p.CurrentUpdate}{(p.Percent is { } pct ? $" {pct:0}%" : string.Empty)}".TrimEnd()));
        _owner.Load(
            ct => _service.InstallAsync([.. selected.Select(u => u.UpdateId)], progress, ct),
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

    private void AppendLog(string line) => _log.Content = string.IsNullOrEmpty(_log.Content) ? line : _log.Content + "\n" + line;

    private void ShowDetails()
    {
        var row = WindowsPanelBase.SelectedRow(_table);
        if (row < 0 || row >= _rows.Count)
        {
            _details.Content = _rows.Count == 0 ? "Press Check to search for updates." : string.Empty;
            return;
        }

        var u = _rows[row].Update;
        var sb = new StringBuilder();
        sb.AppendLine(u.Title);
        sb.AppendLine();
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
