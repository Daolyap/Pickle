using Pickle.Abstractions;
using Pickle.Abstractions.Services;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// Disks (Alt+D, <c>pk disks [folder]</c>): volumes with usage bars and (Linux) per-device throughput, and an
/// Analyze tab — a background, cancellable du-like scan of a volume or folder with drill-down (Enter) and back
/// (Backspace/Left). F2 opens the selected folder in the shell; on Windows F7/F9 open Disk Cleanup / Disk Management.
/// </summary>
public sealed class DisksPanel : SystemPanelBase
{
    public const string PanelId = "disks";

    internal static readonly TimeSpan VolumeInterval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan IoInterval = TimeSpan.FromSeconds(1);

    private readonly IDiskMonitor? _monitor;
    private readonly View _volumesTab = new() { Title = "Volumes", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly View _analyzeTab = new() { Title = "Analyze", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
    private readonly SortableTable<DiskVolume> _volumes;
    private const int MaxIoRows = 8;

    private readonly MetersView _io = new() { X = 0, Y = Pos.AnchorEnd(0), Height = 0, LabelWidth = 10, ValueWidth = 24 };
    private readonly Label _ioTitle = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly Label _location = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private readonly Label _scanStatus = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = 1 };
    private readonly SortableTable<DiskUsageNode> _entries;
    private readonly Dictionary<string, (SampleHistory Read, SampleHistory Write)> _ioHistory = new(StringComparer.Ordinal);
    private DiskUsageNode? _root;
    private DiskUsageNode? _current;
    private CancellationTokenSource? _scan;
    private int _loadingVolumes;
    private int _sampling;

    public DisksPanel(PanelContext context)
        : base(context, "Disks")
    {
        _monitor = Pickle.Services.Get<IDiskMonitor>();
        _volumes = new SortableTable<DiskVolume>(VolumeColumns(), v => v.Name) { Schemes = Schemes };
        _entries = new SortableTable<DiskUsageNode>(EntryColumns(), n => n.FullPath + (n.IsGroup ? "\0group" : string.Empty)) { Y = 2, Schemes = Schemes };
        _io.Schemes = Schemes;
        if (_monitor is null)
        {
            Body.Add(new Label { X = 1, Y = 1, Text = "Disk monitoring is not available in this session." });
            return;
        }

        if (_monitor.SupportsIoRates)
        {
            _volumes.Height = Dim.Fill(1);
            _ioTitle.Text = "Disk I/O";
            _volumesTab.Add(_volumes, _ioTitle, _io);
        }
        else
        {
            _volumesTab.Add(_volumes);
        }

        _volumes.Table.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                if (_volumes.Selected is { } volume)
                {
                    Analyze(volume.Name);
                }

                key.Handled = true;
            }
        };
        _entries.SortBy(1, descending: true);
        _location.Text = "Select a volume and press Enter (or F3 for any folder) to see what uses the space.";
        _analyzeTab.Add(_location, _scanStatus, _entries);

        Body.Add(CreateTabs(_volumesTab, _analyzeTab));
        TabKeys(_volumes.Table);
        TabKeys(_entries.Table, HandleAnalyzeKey);

        AddNote("Enter Open");
        AddHint(Key.F3, "Folder…", AnalyzeFolder);
        AddHint(Key.F2, "cd", OpenInShell);
        if (_monitor.SupportsSystemTools)
        {
            AddHint(Key.F7, "Disk Cleanup", DiskCleanup);
            AddHint(Key.F9, "Disk Mgmt", DiskManagement);
        }

        AddHint(Key.F8, "Stop", StopScan);
        AddHint(Key.F5, "Refresh", Refresh);
        AddNote("←→ Tabs");

        Every(VolumeInterval, RefreshVolumes);
        if (_monitor.SupportsIoRates)
        {
            Every(IoInterval, SampleIo);
        }

        _volumes.Table.SetFocus();
    }

    internal SortableTable<DiskVolume> Volumes => _volumes;

    internal SortableTable<DiskUsageNode> Entries => _entries;

    internal MetersView Io => _io;

    internal View VolumesTab => _volumesTab;

    internal View AnalyzeTab => _analyzeTab;

    internal DiskUsageNode? Current => _current;

    internal bool Scanning => _scan is not null;

    internal string LocationText => _location.Text;

    internal string ScanStatusText => _scanStatus.Text;

    internal void Analyze(string path)
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        StopScan();
        ShowTab(_analyzeTab);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _scan = cts;
        _root = _current = null;
        _entries.SetItems([]);
        _location.Text = path;
        _scanStatus.Text = "Scanning…";
        var progress = new Progress(this, p => _scanStatus.Text =
            $"Scanning… {SystemFormat.Count(p.Directories)} folders · {SystemFormat.Count(p.Files)} files · {SystemFormat.Bytes(p.Bytes)}  ·  F8 stops  ·  {p.CurrentPath}");
        _ = Task.Run(async () =>
        {
            try
            {
                var root = await monitor.AnalyzeAsync(path, progress, cts.Token).ConfigureAwait(false);
                OnUi(() =>
                {
                    if (_scan == cts)
                    {
                        _root = root;
                        Show(root);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                OnUi(() =>
                {
                    if (_scan == cts)
                    {
                        _scanStatus.Text = "Scan stopped.";
                    }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OnUi(() =>
                {
                    if (_scan == cts)
                    {
                        _scanStatus.Text = "✗ " + ex.Message;
                    }
                });
            }
            finally
            {
                OnUi(() =>
                {
                    if (_scan == cts)
                    {
                        _scan = null;
                    }
                });
                cts.Dispose();
            }
        });
    }

    internal void StopScan()
    {
        try
        {
            _scan?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void Show(DiskUsageNode node)
    {
        _current = node;
        _entries.SetItems(node.Children);
        if (node.Children.Count > 0)
        {
            _entries.Select(_ => true);
        }

        _location.Text = $"{node.FullPath}  ·  {SystemFormat.Bytes(node.Size)}";
        var skipped = node.Skipped > 0 ? $"  ·  {SystemFormat.Count(node.Skipped)} skipped (links, other drives, no access)" : string.Empty;
        _scanStatus.Text = $"{SystemFormat.Count(node.FileCount)} files · {SystemFormat.Count(Math.Max(0, node.DirectoryCount - 1))} folders{skipped}  ·  Enter opens · Backspace/← up";
    }

    internal bool Descend()
    {
        if (_entries.Selected is { IsDirectory: true } directory)
        {
            Show(directory);
            return true;
        }

        return false;
    }

    internal bool Ascend()
    {
        if (_current?.Parent is not { } parent)
        {
            return false;
        }

        var from = _current;
        Show(parent);
        _entries.Select(n => ReferenceEquals(n, from));
        return true;
    }

    internal void OpenInShell()
    {
        string? directory = null;
        if (CurrentTab == _analyzeTab && _current is not null)
        {
            directory = _entries.Selected is { IsDirectory: true } selected ? selected.FullPath : _current.FullPath;
        }
        else if (_volumes.Selected is { } volume)
        {
            directory = volume.Name;
        }

        if (directory is not null)
        {
            Complete(new PanelResult(PanelResultKind.ChangeDirectory, directory));
        }
    }

    internal void RefreshVolumes()
    {
        if (_monitor is not { } monitor || Interlocked.Exchange(ref _loadingVolumes, 1) == 1)
        {
            return;
        }

        var token = Lifetime;
        _ = Task.Run(async () =>
        {
            try
            {
                var volumes = await monitor.GetVolumesAsync(token).ConfigureAwait(false);
                OnUi(() => _volumes.SetItems(volumes));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("disks", "reading volumes failed", ex);
            }
            finally
            {
                Volatile.Write(ref _loadingVolumes, 0);
            }
        });
    }

    internal void ApplyIo(IReadOnlyList<DiskIoSample> samples)
    {
        var rows = new List<MeterRow>();
        foreach (var sample in samples)
        {
            if (!_ioHistory.TryGetValue(sample.Device, out var history))
            {
                history = (new SampleHistory(), new SampleHistory());
                _ioHistory[sample.Device] = history;
            }

            history.Read.Add(sample.ReadRate);
            history.Write.Add(sample.WriteRate);
            rows.Add(new MeterRow($"{sample.Device} read", SystemFormat.Rate(sample.ReadRate), history.Read.Values));
            rows.Add(new MeterRow($"{sample.Device} write", SystemFormat.Rate(sample.WriteRate), history.Write.Values));
        }

        var shown = Math.Min(rows.Count, MaxIoRows);
        _io.SetRows(rows);
        if (_io.Frame.Height != shown)
        {
            _io.Height = shown;
            _io.Y = Pos.AnchorEnd(shown);
            _ioTitle.Y = Pos.AnchorEnd(shown + 1);
            _volumes.Height = Dim.Fill(shown + 1);
        }

        _ioTitle.Text = samples.Count == 0 ? "Disk I/O: no active devices" : "Disk I/O";
    }

    protected override void OnOpened()
    {
        RefreshVolumes();
        if (_monitor?.SupportsIoRates == true)
        {
            SampleIo();
        }

        if (Context.Argument is { Length: > 0 } folder)
        {
            Analyze(folder);
        }
    }

    protected override void OnTabChanged(View page)
    {
        if (page == _analyzeTab)
        {
            _entries.Table.SetFocus();
        }
        else if (page == _volumesTab)
        {
            _volumes.Table.SetFocus();
        }
    }

    private bool HandleAnalyzeKey(Key key)
    {
        if (key == Key.Enter)
        {
            Descend();
            return true;
        }

        return (key == Key.Backspace || key == Key.CursorLeft) && Ascend();
    }

    private void AnalyzeFolder()
    {
        var initial = _current?.FullPath ?? Pickle.Shell.CurrentDirectory;
        var path = Prompt("Analyze folder", "Folder to scan:", initial);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var full = Path.GetFullPath(path.Trim(), Pickle.Shell.CurrentDirectory);
        if (!Directory.Exists(full))
        {
            Fail($"Folder not found: {full}");
            return;
        }

        Analyze(full);
    }

    private void Refresh()
    {
        RefreshVolumes();
        if (CurrentTab == _analyzeTab && _root is not null && _scan is null)
        {
            Analyze(_current?.FullPath ?? _root.FullPath);
        }
    }

    private void SampleIo()
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
                var samples = await monitor.SampleIoAsync(token).ConfigureAwait(false);
                OnUi(() => ApplyIo(samples));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Pickle.Log.Warn("disks", "sampling disk I/O failed", ex);
            }
            finally
            {
                Volatile.Write(ref _sampling, 0);
            }
        });
    }

    private void DiskCleanup()
    {
        if (_monitor is { SupportsSystemTools: true } monitor)
        {
            var volume = _volumes.Selected?.Name;
            RunInBackground(ct => monitor.OpenDiskCleanupAsync(volume, ct), ToolDone, "opening Disk Cleanup…");
        }
    }

    private void DiskManagement()
    {
        if (_monitor is { SupportsSystemTools: true } monitor)
        {
            RunInBackground(monitor.OpenDiskManagementAsync, ToolDone, "opening Disk Management…");
        }
    }

    private void ToolDone(DiskToolResult result)
    {
        if (!result.Success)
        {
            Fail(result.Message);
        }
    }

    private List<TableColumn<DiskVolume>> VolumeColumns() =>
    [
        new() { Header = "Volume", Text = v => v.Name, MinWidth = 6, MaxWidth = 24 },
        new() { Header = "Label", Text = v => v.Label ?? string.Empty, MaxWidth = 16 },
        new() { Header = "FS", Text = v => v.FileSystem ?? string.Empty, MaxWidth = 8 },
        new() { Header = "Type", Text = v => v.Type, MaxWidth = 9 },
        new() { Header = "Size", Text = v => SystemFormat.Bytes(v.TotalBytes), SortKey = v => v.TotalBytes, Numeric = true, MinWidth = 9, MaxWidth = 9 },
        new() { Header = "Free", Text = v => SystemFormat.Bytes(v.FreeBytes), SortKey = v => v.FreeBytes, Numeric = true, MinWidth = 9, MaxWidth = 9 },
        new()
        {
            Header = "Used",
            Text = v => $"{SystemFormat.Bar(v.UsedFraction, 12)} {SystemFormat.Percent(v.UsedFraction * 100),6}",
            SortKey = v => v.UsedFraction,
            Numeric = true,
            MinWidth = 19,
            Color = v => v.UsedFraction >= 0.95 ? Schemes.ErrorText.Foreground : v.UsedFraction >= 0.85 ? Schemes.Warning.Foreground : null,
        },
    ];

    private List<TableColumn<DiskUsageNode>> EntryColumns() =>
    [
        new()
        {
            Header = "Name",
            Text = n => n.IsDirectory ? n.Name + Path.DirectorySeparatorChar : n.Name,
            MinWidth = 16,
            MaxWidth = 40,
            Color = n => n.IsDirectory ? Schemes.Accent.Foreground : n.IsGroup ? Schemes.Muted.Foreground : null,
        },
        new() { Header = "Size", Text = n => SystemFormat.Bytes(n.Size), SortKey = n => n.Size, Numeric = true, MinWidth = 9, MaxWidth = 9 },
        new()
        {
            Header = "Share",
            Text = n => Share(n) is var share ? $"{SystemFormat.Bar(share, 10)} {SystemFormat.Percent(share * 100),6}" : string.Empty,
            SortKey = n => n.Size,
            Numeric = true,
            MinWidth = 17,
            MaxWidth = 17,
        },
        new() { Header = "Files", Text = n => SystemFormat.Count(n.FileCount), SortKey = n => n.FileCount, Numeric = true, MinWidth = 7, MaxWidth = 10 },
        new() { Header = "Type", Text = n => n.IsGroup ? "files" : n.IsDirectory ? "folder" : Path.GetExtension(n.Name).TrimStart('.') },
    ];

    private double Share(DiskUsageNode node) => _current is { Size: > 0 } parent ? (double)node.Size / parent.Size : 0;

    private sealed class Progress(DisksPanel owner, Action<DiskScanProgress> report) : IProgress<DiskScanProgress>
    {
        public void Report(DiskScanProgress value) => owner.OnUi(() => report(value));
    }
}
