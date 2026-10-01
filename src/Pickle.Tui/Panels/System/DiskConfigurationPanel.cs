using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// Disk Configuration (Alt+Shift+C, <c>pk diskconfig</c>): every disk drawn as a strip, its partitions and free space as a
/// table, and the changes you plan on them (initialize, new volume, format, resize, delete, drive letter, label, check,
/// optimize, online/offline/read-only, wipe) collected as a list you review and apply together: one administrator
/// prompt, in order, stopping at the first problem. The disks you see include the planned changes, so
/// initialize → new volume → format is one pass. Windows and the boot files are never offered.
/// </summary>
public sealed class DiskConfigurationPanel : SystemPanelBase
{
    public const string PanelId = "diskconfig";

    private const int PendingHeight = 8;
    private const string EraseWord = "ERASE";

    private readonly IDiskConfigurationService? _service;
    private readonly PreviewPane _map = new("Disks") { X = 0, Y = 0, Width = Dim.Fill(), Height = 5 };
    private readonly SortableTable<LayoutRow>? _table;
    private readonly PreviewPane _pendingPane = new("Planned changes") { X = 0, Y = Pos.AnchorEnd(PendingHeight), Width = Dim.Fill(), Height = PendingHeight };
    private readonly List<DiskOperation> _pending = [];
    private IReadOnlyList<PhysicalDisk> _live = [];
    private IReadOnlyList<PhysicalDisk> _projected = [];
    private int? _focusDisk;
    private bool _applying;

    public DiskConfigurationPanel(PanelContext context)
        : base(context, "Disk Configuration")
    {
        _service = Pickle.Services.Get<IDiskConfigurationService>();
        _map.Schemes = Schemes;
        _pendingPane.Schemes = Schemes;
        if (_service is not { IsSupported: true })
        {
            Body.Add(new Terminal.Gui.Views.Label { X = 1, Y = 1, Text = "Disk configuration needs Windows (the Storage module and its administrator prompt)." });
            return;
        }

        if (int.TryParse(context.Argument, out var disk))
        {
            _focusDisk = disk;
        }

        _table = new SortableTable<LayoutRow>(Columns(), r => r.Order) { Y = Pos.Bottom(_map), Height = Dim.Fill(PendingHeight), Schemes = Schemes };
        Body.Add(_map, _table, _pendingPane);
        TabKeys(
            _table.Table,
            key =>
            {
                if (key != Key.Enter)
                {
                    return false;
                }

                ChooseAction();
                return true;
            });

        AddNote("Enter Plan");
        AddHint(Key.F3, "Review", Review);
        AddHint(Key.F6, "To shell", ToShell);
        AddHint(Key.F7, "Discard", Discard);
        AddHint(Key.F8, "Undo", Undo);
        AddHint(Key.F9, "Apply", Apply);
        AddHint(Key.F5, "Refresh", Refresh);
        _table.Table.SetFocus();
        ShowPending();
    }

    internal SortableTable<LayoutRow>? Rows => _table;

    internal IReadOnlyList<DiskOperation> Pending => _pending;

    internal IReadOnlyList<PhysicalDisk> Projected => _projected;

    internal string MapText => _map.PlainText;

    internal string PendingText => _pendingPane.PlainText;

    /// <summary>Tests replace the text prompt (title, label) → text, or null for cancel.</summary>
    internal Func<string, string, string?>? PromptHook { get; set; }

    private string? AskText(string title, string label, string initial = "") => PromptHook is { } hook ? hook(title, label) : Prompt(title, label, initial);

    protected override void OnOpened() => Refresh();

    internal void Refresh()
    {
        if (_service is null)
        {
            return;
        }

        RunInBackground(_service.GetDisksAsync, disks =>
        {
            _live = disks;
            Rebuild(announceDropped: true);
            if (_focusDisk is { } number && _table is not null)
            {
                _table.Select(r => r.IsDisk && r.Disk.Number == number);
                _focusDisk = null;
            }
        },
        "reading disks…");
    }

    // The planned changes are replayed over the disks as they are now; one that no longer fits (the disk changed meanwhile) is dropped.
    private void Rebuild(bool announceDropped)
    {
        var projected = _live;
        var kept = new List<DiskOperation>();
        var dropped = new List<string>();
        foreach (var operation in _pending)
        {
            try
            {
                projected = DiskOperationRules.Project(operation, projected);
                kept.Add(operation);
            }
            catch (ArgumentException ex)
            {
                dropped.Add($"{DiskOperationRules.Describe(operation)}: {ex.Message}");
            }
        }

        _pending.Clear();
        _pending.AddRange(kept);
        _projected = projected;
        _table?.SetItems(DiskLayoutRows.Build(projected));
        var disks = _map.Viewport.Width > 0 ? _map.Viewport.Width : 80;
        var lines = DiskMap.Render(projected, disks);
        _map.Show("Disks", lines);
        _map.Height = Math.Clamp(lines.Count + 2, 5, 16);
        ShowPending();
        if (announceDropped && dropped.Count > 0)
        {
            Tell("Planned changes dropped", "These no longer fit the disks as they are now:\n\n" + string.Join('\n', dropped));
        }
    }

    internal void ChooseAction()
    {
        if (_table?.Selected is not { } row)
        {
            return;
        }

        var actions = DiskActions.For(row, _projected);
        if (actions.Count == 0)
        {
            Tell("Nothing to plan", DiskActions.WhyNothing(row));
            return;
        }

        var choice = Choose(DiskLayoutRows.Name(row).Trim(), [.. actions.Select(a => a.Label)]);
        if (actions.FirstOrDefault(a => a.Label == choice) is not { } action)
        {
            return;
        }

        if (!action.NeedsRange)
        {
            Plan(row, action, null);
            return;
        }

        var partition = row.Partition!;
        RunInBackground(
            ct => _service!.GetResizeRangeAsync(partition.DiskNumber, partition.PartitionNumber, ct),
            range => Plan(row, action, range),
            "reading the supported size…");
    }

    private void Plan(LayoutRow row, DiskActionSpec action, DiskResizeRange? range)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in action.Inputs)
        {
            var label = input.Label;
            if (input.Id == "size" && range is not null && action.NeedsRange)
            {
                label += $"  [Windows allows {DiskOperationRules.FormatBytes(range.MinimumBytes)} to {DiskOperationRules.FormatBytes(range.MaximumBytes)}]";
            }

            var answer = input.Kind == DiskInputKind.Choice
                ? Choose(label, [.. input.Choices ?? []])
                : AskText(DiskLayoutRows.Name(row).Trim(), label, input.Default);
            if (answer is null)
            {
                return;
            }

            values[input.Id] = answer;
        }

        if (action.NeedsRange)
        {
            values["_max"] = (range?.MaximumBytes ?? row.Partition!.Size + row.Disk.UnallocatedBytes).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        try
        {
            var operation = action.Build(values);
            if (range is not null && operation.Kind == DiskOperationKind.ResizePartition && (operation.SizeBytes < range.MinimumBytes || operation.SizeBytes > range.MaximumBytes))
            {
                throw new ArgumentException($"Windows allows {DiskOperationRules.FormatBytes(range.MinimumBytes)} to {DiskOperationRules.FormatBytes(range.MaximumBytes)} for this partition.");
            }

            Add(operation);
        }
        catch (ArgumentException ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>Plans <paramref name="operation"/> on top of the disks as they will be after the planned changes.</summary>
    internal bool Add(DiskOperation operation)
    {
        try
        {
            _projected = DiskOperationRules.Project(operation, _projected);
        }
        catch (ArgumentException ex)
        {
            Fail(ex.Message);
            return false;
        }

        _pending.Add(operation);
        Rebuild(announceDropped: false);
        return true;
    }

    internal void Undo()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        _pending.RemoveAt(_pending.Count - 1);
        Rebuild(announceDropped: false);
    }

    internal void Discard()
    {
        if (_pending.Count == 0 || !Ask("Discard", $"Discard all {_pending.Count} planned change(s)? Nothing has been changed on the disks."))
        {
            return;
        }

        _pending.Clear();
        Rebuild(announceDropped: false);
    }

    internal void Review()
    {
        if (_pending.Count == 0)
        {
            Tell("Planned changes", "Nothing is planned. Enter on a disk, partition or free space plans a change.");
            return;
        }

        ShowText("Planned changes: the same as these PowerShell commands, in this order", ReviewLines(_pending));
    }

    internal static IReadOnlyList<string> ReviewLines(IReadOnlyList<DiskOperation> operations)
    {
        var lines = new List<string>();
        for (var i = 0; i < operations.Count; i++)
        {
            lines.Add($"{i + 1}. {DiskOperationRules.Describe(operations[i])}{(DiskOperationRules.IsDestructive(operations[i]) ? "   ← erases data" : string.Empty)}");
            lines.Add("     " + DiskOperationRules.ToCommand(operations[i]));
        }

        return lines;
    }

    /// <summary>Closes the panel and puts the equivalent commands on the input line, to run in an administrator shell of your own.</summary>
    internal void ToShell()
    {
        if (_pending.Count == 0)
        {
            Tell("Planned changes", "Nothing is planned.");
            return;
        }

        Complete(new PanelResult(PanelResultKind.ReplaceInput, string.Join(" && ", _pending.Select(DiskOperationRules.ToCommand))));
    }

    internal void Apply()
    {
        if (_service is null || _applying)
        {
            return;
        }

        if (_pending.Count == 0)
        {
            Tell("Planned changes", "Nothing is planned.");
            return;
        }

        var operations = _pending.ToList();
        var erasing = operations.Where(DiskOperationRules.IsDestructive).ToList();
        var summary = string.Join('\n', operations.Select((o, i) => $"{i + 1}. {DiskOperationRules.Describe(o)}"));
        var approval = _service.NeedsElevation ? "\n\nWindows will ask for administrator approval once." : string.Empty;
        if (!Ask("Apply changes", $"Apply these {operations.Count} change(s) in order? They stop at the first one that fails.\n\n{summary}{approval}"))
        {
            return;
        }

        if (erasing.Count > 0
            && !string.Equals(AskText("Erase data", $"{erasing.Count} of these erase data for good. Type {EraseWord} to go on:")?.Trim(), EraseWord, StringComparison.Ordinal))
        {
            Tell("Cancelled", "Nothing was changed.");
            return;
        }

        _applying = true;
        RunInBackground<IReadOnlyList<DiskOperationResult>?>(
            async ct =>
            {
                try
                {
                    return await _service.ApplyAsync(operations, new Progress<string>(text => OnUi(() => _pendingPane.ShowMessage("Applying…", text))), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
            },
            results => Applied(operations, results),
            "changing disks…");
    }

    private void Applied(IReadOnlyList<DiskOperation> operations, IReadOnlyList<DiskOperationResult>? results)
    {
        _applying = false;
        if (results is null)
        {
            Tell("Cancelled", "The administrator prompt was declined. Nothing was changed.");
            ShowPending();
            return;
        }

        var done = results.TakeWhile(r => r.Success).Count();
        _pending.RemoveRange(0, Math.Min(done, _pending.Count));
        var lines = results.Select(r => $"{(r.Success ? "✓" : "✗")} {DiskOperationRules.Describe(r.Operation)}: {r.Message}").ToList();
        if (done < operations.Count)
        {
            lines.Add(string.Empty);
            lines.Add($"{operations.Count - done} change(s) were not applied and stay planned: undo the first one (F8) or fix the problem and apply again.");
        }

        Tell(done == operations.Count ? "Done" : "Stopped", string.Join('\n', lines));
        Refresh();
    }

    private void ShowPending()
    {
        if (_pending.Count == 0)
        {
            _pendingPane.Show(
                "Planned changes",
                [
                    "Nothing planned. Enter on a disk, partition or free space plans a change.",
                    "Changes are collected here; F3 reviews them as PowerShell, F9 applies them all together.",
                    _service?.NeedsElevation == true ? "Applying asks for administrator approval (UAC) once; reading disks never does." : string.Empty,
                ]);
            return;
        }

        _pendingPane.Show(
            $"Planned changes ({_pending.Count}): F9 applies, F8 undoes the last, F7 discards",
            _pending.Select((o, i) => new PreviewLine($"{i + 1}. {DiskOperationRules.Describe(o)}", DiskOperationRules.IsDestructive(o) ? Schemes.Warning.Foreground : null)));
    }

    private string Pending_(LayoutRow row)
    {
        if (row.IsDisk)
        {
            var count = _pending.Count(o => o.DiskNumber == row.Disk.Number);
            return count == 0 ? string.Empty : $"{count} planned";
        }

        if (row.Partition is not { } partition)
        {
            return string.Empty;
        }

        return DiskOperationRules.IsPlanned(partition)
            ? "new"
            : string.Join(", ", _pending.Where(o => o.DiskNumber == partition.DiskNumber && o.PartitionNumber == partition.PartitionNumber).Select(o => Verb(o.Kind)));
    }

    private static string Verb(DiskOperationKind kind) => kind switch
    {
        DiskOperationKind.FormatVolume => "format",
        DiskOperationKind.ResizePartition => "resize",
        DiskOperationKind.SetDriveLetter => "letter",
        DiskOperationKind.SetLabel => "label",
        DiskOperationKind.CheckVolume => "check",
        DiskOperationKind.OptimizeVolume => "optimize",
        _ => kind.ToString(),
    };

    private List<TableColumn<LayoutRow>> Columns() =>
    [
        new() { Header = "Disk / partition", Text = DiskLayoutRows.Name, SortKey = r => r.Order, MinWidth = 22, MaxWidth = 44, Color = r => r.IsDisk ? Schemes.Accent.Foreground : r.Free ? Schemes.Muted.Foreground : null },
        new() { Header = "Type", Text = DiskLayoutRows.Kind, SortKey = r => r.Order, MaxWidth = 32 },
        new() { Header = "Size", Text = r => SystemFormat.Bytes(DiskLayoutRows.Size(r)), SortKey = r => r.Order, MinWidth = 9, MaxWidth = 9 },
        new() { Header = "Status", Text = DiskLayoutRows.Status, SortKey = r => r.Order, MaxWidth = 34, Color = r => r.Disk.IsOffline ? Schemes.Warning.Foreground : null },
        new() { Header = "Planned", Text = Pending_, SortKey = r => r.Order, Color = _ => Schemes.Accent.Foreground },
    ];
}
