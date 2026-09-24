using System.Globalization;
using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// Processes (Alt+P, <c>pk top</c>): machine CPU and memory with history, and a sortable, filterable process table
/// refreshed every 1.5 s. Enter shows details; Del ends the process, F8 its whole tree, F4 changes its priority.
/// </summary>
public sealed class ProcessesPanel : SystemPanelBase
{
    public const string PanelId = "processes";

    internal const int CpuColumn = 3;
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly (string Label, ProcessPriority Priority)[] Priorities =
    [
        ("High", ProcessPriority.High),
        ("Above normal", ProcessPriority.AboveNormal),
        ("Normal", ProcessPriority.Normal),
        ("Below normal", ProcessPriority.BelowNormal),
        ("Idle", ProcessPriority.Idle),
    ];

    private readonly IProcessMonitor? _monitor;
    private readonly MetersView _meters = new() { X = 0, Y = 0, Height = 2 };
    private readonly Label _summary = new() { X = 0, Y = 2, Width = Dim.Fill(), Height = 1 };
    private readonly SortableTable<ProcessSample> _table;
    private readonly SampleHistory _cpu = new();
    private readonly SampleHistory _memory = new();
    private ProcessSnapshot? _snapshot;
    private string? _status;
    private int _sampling;

    public ProcessesPanel(PanelContext context)
        : base(context, "Processes")
    {
        _monitor = Pickle.Services.Get<IProcessMonitor>();
        _table = new SortableTable<ProcessSample>(Columns(), p => (p.Id, p.StartTime), p => $"{p.Name} {p.User}") { Y = 3, Schemes = Schemes };
        _meters.Schemes = Schemes;
        if (_monitor is null)
        {
            Body.Add(new Label { X = 1, Y = 1, Text = "Process monitoring is not available in this session." });
            return;
        }

        _table.SortBy(CpuColumn, descending: true);
        if (context.Argument is { Length: > 0 } filter)
        {
            _table.FilterText = filter;
        }

        Body.Add(_meters, _summary, _table);
        _table.Table.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                ShowDetails();
                key.Handled = true;
            }
            else if (key == Key.Delete)
            {
                End(tree: false);
                key.Handled = true;
            }
        };
        _table.SelectionChanged += (_, _) => UpdateSummary();

        AddNote("Enter Details");
        AddNote("Del End");
        AddHint(Key.F8, "End tree", () => End(tree: true));
        AddHint(Key.F4, "Priority", ChangePriority);
        AddHint(Key.F6, "Sort", () => _table.HandleKey(Key.F6));
        Every(RefreshInterval, Refresh);
        _table.Table.SetFocus();
    }

    internal SortableTable<ProcessSample> Table => _table;

    internal MetersView Meters => _meters;

    internal string SummaryText => _summary.Text;

    internal static IReadOnlyList<string> PriorityLabels => [.. Priorities.Select(p => p.Label)];

    internal static List<string> DetailLines(ProcessDetails d)
    {
        string Or(string? value) => string.IsNullOrEmpty(value) ? "—" : value;
        return
        [
            $"Name:            {d.Name}",
            $"Process id:      {d.Id.ToString(CultureInfo.InvariantCulture)}",
            $"Parent:          {(d.ParentId is { } parent ? $"{d.ParentName ?? "?"} ({parent.ToString(CultureInfo.InvariantCulture)})" : "—")}",
            $"User:            {Or(d.User)}",
            $"Started:         {Or(SystemFormat.Time(d.StartTime, DateTimeOffset.Now))}",
            $"Priority:        {(d.Priority is { } priority ? PriorityLabel(priority) : "—")}",
            $"CPU time:        {(d.TotalCpuTime is { } cpu ? SystemFormat.Duration(cpu) : "—")}",
            $"Working set:     {SystemFormat.Bytes(d.WorkingSet)}",
            $"Private memory:  {(d.PrivateMemory is { } bytes ? SystemFormat.Bytes(bytes) : "—")}",
            $"Threads:         {SystemFormat.Count(d.Threads)}",
            $"Path:            {Or(d.Path)}",
            string.Empty,
            "Command line:",
            d.CommandLine ?? "(not available)",
        ];
    }

    internal void Apply(ProcessSnapshot snapshot)
    {
        _snapshot = snapshot;
        _cpu.Add(snapshot.CpuPercent);
        var memory = snapshot.MemoryTotal > 0 ? (double)snapshot.MemoryUsed / snapshot.MemoryTotal : 0;
        _memory.Add(memory * 100);
        _meters.SetRows(
        [
            new MeterRow("CPU", SystemFormat.Percent(snapshot.CpuPercent), _cpu.Values, 100, snapshot.CpuPercent / 100),
            new MeterRow(
                "Memory",
                snapshot.MemoryTotal > 0 ? $"{SystemFormat.Bytes(snapshot.MemoryUsed)} / {SystemFormat.Bytes(snapshot.MemoryTotal)}" : "—",
                _memory.Values,
                100,
                memory),
        ]);
        _table.SetItems(snapshot.Processes);
        UpdateSummary();
    }

    internal void End(bool tree)
    {
        if (_monitor is null || _table.Selected is not { } process)
        {
            return;
        }

        var what = $"{process.Name} ({process.Id.ToString(CultureInfo.InvariantCulture)})";
        string message;
        if (!tree)
        {
            message = $"End {what}?\n\nUnsaved work in it will be lost.";
        }
        else if (_snapshot?.Processes.Any(p => p.ParentId is not null) == true)
        {
            var children = Descendants(process).Count;
            message = $"End {what} and its {children.ToString(CultureInfo.InvariantCulture)} child process{(children == 1 ? string.Empty : "es")}?\n\nUnsaved work in them will be lost.";
        }
        else
        {
            message = $"End {what} and all of its child processes?\n\nUnsaved work in them will be lost.";
        }

        if (!Ask(tree ? "End process tree" : "End process", message))
        {
            return;
        }

        var monitor = _monitor;
        RunInBackground(ct => monitor.KillAsync(process, tree, ct), Done, "ending…");
    }

    internal void ChangePriority()
    {
        if (_monitor is null || _table.Selected is not { } process)
        {
            return;
        }

        var choice = Choose($"Priority of {process.Name} ({process.Id.ToString(CultureInfo.InvariantCulture)})", PriorityLabels);
        var index = Array.FindIndex(Priorities, p => p.Label == choice);
        if (index < 0)
        {
            return;
        }

        var monitor = _monitor;
        var priority = Priorities[index].Priority;
        RunInBackground(ct => monitor.SetPriorityAsync(process, priority, ct), Done, "changing priority…");
    }

    internal void ShowDetails()
    {
        if (_monitor is null || _table.Selected is not { } process)
        {
            return;
        }

        var monitor = _monitor;
        RunInBackground(
            ct => monitor.GetDetailsAsync(process, ct),
            details =>
            {
                if (details is null)
                {
                    Fail($"{process.Name} ({process.Id.ToString(CultureInfo.InvariantCulture)}) is no longer running.");
                }
                else
                {
                    ShowText($"{details.Name} ({details.Id.ToString(CultureInfo.InvariantCulture)})", DetailLines(details));
                }
            },
            "reading details…");
    }

    internal void Refresh()
    {
        if (_monitor is not { } monitor || Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        var token = Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await monitor.SampleAsync(token).ConfigureAwait(false);
                OnUi(() => Apply(snapshot));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("processes", "sampling processes failed", ex);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
    }

    protected override void OnOpened() => Refresh();

    private static string PriorityLabel(ProcessPriority priority) => Priorities.First(p => p.Priority == priority).Label;

    private void Done(ProcessActionResult result)
    {
        if (result.Success)
        {
            _status = result.Message;
            UpdateSummary();
        }
        else
        {
            Fail(result.Message);
        }

        Refresh();
    }

    private List<ProcessSample> Descendants(ProcessSample root)
    {
        var children = (_snapshot?.Processes ?? []).Where(p => p.ParentId is not null).ToLookup(p => p.ParentId!.Value);
        var result = new List<ProcessSample>();
        var seen = new HashSet<int> { root.Id };
        var queue = new Queue<int>();
        queue.Enqueue(root.Id);
        while (queue.TryDequeue(out var id))
        {
            foreach (var child in children[id])
            {
                if (seen.Add(child.Id))
                {
                    result.Add(child);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    private void UpdateSummary()
    {
        if (_snapshot is not { } snapshot)
        {
            _summary.Text = "Sampling…";
            return;
        }

        var threads = snapshot.Processes.Sum(p => (long)p.Threads);
        var text = $"{SystemFormat.Count(snapshot.Processes.Count)} processes · {SystemFormat.Count(threads)} threads · sorted by {_table.SortDescription}";
        _summary.Text = _status is null ? text : $"{text} · {_status}";
    }

    private List<TableColumn<ProcessSample>> Columns() =>
    [
        new() { Header = "Name", Text = p => p.Name, MinWidth = 16, MaxWidth = 28 },
        new() { Header = "PID", Text = p => p.Id.ToString(CultureInfo.InvariantCulture), SortKey = p => p.Id, Numeric = true, MinWidth = 7, MaxWidth = 8 },
        new() { Header = "User", Text = p => p.User ?? string.Empty, MinWidth = 6, MaxWidth = 12 },
        new()
        {
            Header = "CPU %",
            Text = p => p.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture),
            SortKey = p => p.CpuPercent,
            Numeric = true,
            MinWidth = 7,
            MaxWidth = 7,
            Color = p => p.CpuPercent >= 80 ? Schemes.ErrorText.Foreground : p.CpuPercent >= 40 ? Schemes.Warning.Foreground : null,
        },
        new() { Header = "Memory", Text = p => SystemFormat.Bytes(p.WorkingSet), SortKey = p => p.WorkingSet, Numeric = true, MinWidth = 10, MaxWidth = 10 },
        new() { Header = "Threads", Text = p => SystemFormat.Count(p.Threads), SortKey = p => p.Threads, Numeric = true, MinWidth = 7, MaxWidth = 8 },
        new() { Header = "Started", Text = p => SystemFormat.Time(p.StartTime, DateTimeOffset.Now), SortKey = p => p.StartTime, Numeric = true, MinWidth = 8 },
    ];
}
