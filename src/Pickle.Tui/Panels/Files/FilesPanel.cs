using System.Collections.Concurrent;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Panels.Files;

/// <summary>
/// Fuzzy file finder (Ctrl+T). Scans the root (panel argument or the current directory) in the background,
/// previews the selection, and inserts the chosen paths (Space marks several) — relative to the current directory
/// when inside it, quoted when needed. In directory mode (Alt+C) Enter changes to the selected directory.
/// </summary>
public sealed class FilesPanel : PanelWindow
{
    /// <summary>Argument prefix that opens the picker in directory mode (optionally followed by a root path).</summary>
    public const string DirectoryModePrefix = "cd:";

    private readonly bool _directoryMode;
    private readonly FilterableList<FileEntry> _list;
    private readonly PreviewPane _preview;
    private readonly ConcurrentQueue<(int Generation, IReadOnlyList<FileEntry> Batch)> _incoming = new();
    private CancellationTokenSource? _scan;
    private int _previewVersion;
    private int _generation;
    private bool _scanning;

    public FilesPanel(PanelContext context)
        : base(context, "Files")
    {
        var argument = context.Argument ?? string.Empty;
        _directoryMode = argument.StartsWith(DirectoryModePrefix, StringComparison.Ordinal);
        if (_directoryMode)
        {
            argument = argument[DirectoryModePrefix.Length..];
        }

        Root = ResolveRoot(argument, Pickle.Shell.CurrentDirectory);
        Options = new FileScanOptions { DirectoriesOnly = _directoryMode };

        _list = new FilterableList<FileEntry>(e => e.Display)
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(55),
            Height = Dim.Fill(),
            MultiSelect = !_directoryMode,
            ItemColor = e => e.IsDirectory ? Schemes.Info.Foreground : null,
            Hint = e => e.IsDirectory ? null : FilePreview.FormatSize(e.Size),
            Schemes = Schemes,
        };
        _preview = new PreviewPane() { X = Pos.Right(_list), Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Schemes = Schemes };
        Body.Add(_list, _preview);

        _list.ItemAccepted += (_, _) => Accept();
        _list.SelectionChanged += (_, entry) => UpdatePreview(entry);
        _list.Filter.KeyDown += (_, key) =>
        {
            if (key == Key.Backspace && _list.FilterText.Length == 0)
            {
                GoUp();
                key.Handled = true;
            }
        };

        AddHint(Key.Enter, _directoryMode ? "Cd" : "Insert", Accept);
        if (!_directoryMode)
        {
            AddHint(Key.O.WithCtrl, "Cd", CdIntoSelection);
        }

        AddHint(Key.CursorRight.WithAlt, "Into dir", Descend);
        AddHint(Key.CursorUp.WithAlt, "Parent", GoUp);
        AddHint(Key.H.WithAlt, "Hidden", () => Rescan(Options with { IncludeHidden = !Options.IncludeHidden }));
        AddHint(Key.I.WithAlt, "Ignored", () => Rescan(Options with { SkipIgnored = !Options.SkipIgnored }));
        Every(TimeSpan.FromMilliseconds(120), DrainIncoming);
        UpdateTitle();
        _list.Filter.SetFocus();
    }

    public string Root { get; private set; }

    public FileScanOptions Options { get; private set; }

    /// <summary>True while the background scan is running.</summary>
    public bool IsScanning => _scanning || !_incoming.IsEmpty;

    internal FilterableList<FileEntry> List => _list;

    internal PreviewPane Preview => _preview;

    /// <summary>
    /// Text to insert for <paramref name="paths"/>: relative to <paramref name="cwd"/> when inside it, single-quoted
    /// for PowerShell when they contain spaces or special characters, separated by spaces.
    /// </summary>
    public static string FormatPaths(IEnumerable<string> paths, string cwd) =>
        string.Join(' ', paths.Select(p => Quote(MakeRelative(p, cwd))));

    public static string MakeRelative(string path, string cwd)
    {
        var full = Path.GetFullPath(path);
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.StartsWith(baseDir + Path.DirectorySeparatorChar, comparison))
        {
            return Path.GetRelativePath(baseDir, full);
        }

        return string.Equals(full, baseDir, comparison) ? "." : full;
    }

    /// <summary>Bare only when PowerShell reads it back as the same single string (not 1kb → 1024); otherwise single-quoted.</summary>
    public static string Quote(string path) => global::Pickle.Wizards.PowerShellQuoting.FormatArgument(path);

    protected override void OnOpened() => Rescan(Options);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scan?.Cancel();
            _scan?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string ResolveRoot(string argument, string cwd)
    {
        var path = argument.Trim().Trim('"', '\'');
        if (path.StartsWith('~'))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        if (path.Length > 0)
        {
            var full = Path.GetFullPath(path, cwd);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory;
    }

    private void Accept()
    {
        if (_directoryMode)
        {
            if (_list.Selected is { IsDirectory: true } dir)
            {
                Complete(new PanelResult(PanelResultKind.ChangeDirectory, dir.FullPath));
            }

            return;
        }

        var chosen = _list.Chosen;
        if (chosen.Count > 0)
        {
            Complete(new PanelResult(PanelResultKind.InsertText, FormatPaths(chosen.Select(e => e.FullPath), Pickle.Shell.CurrentDirectory)));
        }
    }

    private void CdIntoSelection()
    {
        if (_list.Selected is { } entry)
        {
            var dir = entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath) ?? Root;
            Complete(new PanelResult(PanelResultKind.ChangeDirectory, dir));
        }
    }

    private void Descend()
    {
        if (_list.Selected is { IsDirectory: true } dir)
        {
            Root = dir.FullPath;
            _list.FilterText = string.Empty;
            Rescan(Options);
        }
    }

    private void GoUp()
    {
        if (Directory.GetParent(Root) is { } parent)
        {
            Root = parent.FullName;
            Rescan(Options);
        }
    }

    private void Rescan(FileScanOptions options)
    {
        Options = options;
        _scan?.Cancel();
        _scan?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _scan = cts;
        var generation = ++_generation;
        _incoming.Clear();
        _list.SetItems([]);
        _list.Status = "scanning…";
        _scanning = true;
        UpdateTitle();
        var root = Root;
        RunInBackground(
            _ =>
            {
                try
                {
                    FileScanner.Scan(root, options, batch => _incoming.Enqueue((generation, batch)), cts.Token);
                }
                catch (OperationCanceledException)
                {
                }

                return Task.FromResult(generation);
            },
            finished =>
            {
                if (finished != _generation)
                {
                    return;
                }

                _scanning = false;
                DrainIncoming();
                _list.Status = null;
            },
            "scanning…");
    }

    private void DrainIncoming()
    {
        if (_incoming.IsEmpty)
        {
            return;
        }

        var batch = new List<FileEntry>();
        while (_incoming.TryDequeue(out var entries))
        {
            if (entries.Generation == _generation)
            {
                batch.AddRange(entries.Batch);
            }
        }

        if (batch.Count > 0)
        {
            _list.AddItems(batch);
        }
    }

    private void UpdatePreview(FileEntry? entry)
    {
        var version = ++_previewVersion;
        if (entry is null)
        {
            _preview.Clear();
            return;
        }

        _ = Task.Run(() => FilePreview.Build(entry.FullPath), Lifetime).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    OnUi(() =>
                    {
                        if (version == _previewVersion)
                        {
                            _preview.Show(t.Result.Title, t.Result.Lines, t.Result.LineNumbers);
                        }
                    });
                }
            },
            TaskScheduler.Default);
    }

    private void UpdateTitle()
    {
        var flags = (Options.IncludeHidden ? " +hidden" : string.Empty) + (Options.SkipIgnored ? string.Empty : " +ignored");
        PanelTitle = $"{(_directoryMode ? "Change directory" : "Files")} — {Root}{flags}";
    }
}
