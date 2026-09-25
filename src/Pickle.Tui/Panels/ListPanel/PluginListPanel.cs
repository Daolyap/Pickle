using System.Collections;
using System.Management.Automation;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Panels.ListPanel;

/// <summary>
/// Generic panel for a <see cref="ListPanelSpec"/>: runs the items script in the main runspace, shows the items
/// (their Name property, else ToString) with a fuzzy filter and a Format-List preview, and runs the spec's actions
/// with <c>$_</c> bound to the selected item. Enter runs the first action; the others get F-keys.
/// An action may return an object/hashtable with a Run, Insert, Replace or Cd key to hand a result to the shell;
/// anything else it outputs is shown in the preview and the list is refreshed. A spec's PreviewScript replaces the
/// Format-List preview; RefreshSeconds reloads the items on a timer.
/// </summary>
public sealed class PluginListPanel : PanelWindow
{
    internal const string ItemsScript = """
        param($__pickleScript)
        foreach ($__pickleItem in @(& ([scriptblock]::Create($__pickleScript)))) {
            $__pickleName = try { $__pickleItem.Name } catch { $null }
            if ($__pickleItem -is [string] -or $null -eq $__pickleName -or "$__pickleName" -eq '') { $__pickleName = "$__pickleItem" }
            [pscustomobject]@{ I = $__pickleItem; D = [string]$__pickleName }
        }
        """;

    internal const string ActionScript = """
        param($__pickleScript, $__pickleItem)
        $__pickleItem | ForEach-Object -Process ([scriptblock]::Create($__pickleScript))
        """;

    internal const string PreviewScript = "param($__pickleItem) $__pickleItem | Format-List -Property * | Out-String -Width 160";

    internal const string CustomPreviewScript = """
        param($__pickleScript, $__pickleItem)
        $__pickleItem | ForEach-Object -Process ([scriptblock]::Create($__pickleScript)) | Out-String -Width 160
        """;

    private static readonly Key[] ActionKeys = [Key.F2, Key.F3, Key.F4, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12];

    private readonly ListPanelSpec _spec;
    private readonly FilterableList<ListItem> _list;
    private readonly PreviewPane _preview;
    private int _previewVersion;

    public PluginListPanel(PanelContext context, ListPanelSpec spec)
        : base(context, spec.Title)
    {
        _spec = spec;
        _list = new FilterableList<ListItem>(i => i.Display)
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(50),
            Height = Dim.Fill(),
            Schemes = Schemes,
        };
        _preview = new PreviewPane("Details") { X = Pos.Right(_list), Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Schemes = Schemes };
        Body.Add(_list, _preview);

        _list.ItemAccepted += (_, item) =>
        {
            if (_spec.Actions.Count > 0)
            {
                RunAction(_spec.Actions.First().Key, item);
            }
        };
        _list.SelectionChanged += (_, item) => UpdatePreview(item);

        var index = 0;
        foreach (var label in _spec.Actions.Keys)
        {
            if (index >= ActionKeys.Length)
            {
                break;
            }

            var actionLabel = label;
            AddHint(ActionKeys[index++], actionLabel, () =>
            {
                if (_list.Selected is { } item)
                {
                    RunAction(actionLabel, item);
                }
            });
        }

        AddHint(Key.F5, "Refresh", Refresh);
        _list.Filter.SetFocus();
    }

    /// <summary>The items currently loaded (for tests).</summary>
    internal FilterableList<ListItem> List => _list;

    internal PreviewPane Preview => _preview;

    public static PanelDescriptor CreateDescriptor(ListPanelSpec spec) => new()
    {
        Id = spec.Id,
        Title = spec.Title,
        Description = string.IsNullOrWhiteSpace(spec.Description) ? "Plugin panel" : spec.Description,
        DefaultKey = spec.DefaultKey,
        CreateView = ctx => new PluginListPanel(ctx, spec),
    };

    protected override void OnOpened()
    {
        Refresh();
        if (_spec.RefreshSeconds is { } seconds and > 0)
        {
            Every(TimeSpan.FromSeconds(seconds), Refresh);
        }
    }

    internal static PanelResult? ToResult(PSObject output)
    {
        string? Get(string key)
        {
            if (output.BaseObject is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value?.ToString();
                    }
                }

                return null;
            }

            return output.BaseObject is PSCustomObject ? output.Properties[key]?.Value?.ToString() : null;
        }

        if (Get("Run") is { } run)
        {
            return new PanelResult(PanelResultKind.RunCommand, run);
        }

        if (Get("Insert") is { } insert)
        {
            return new PanelResult(PanelResultKind.InsertText, insert);
        }

        if (Get("Replace") is { } replace)
        {
            return new PanelResult(PanelResultKind.ReplaceInput, replace);
        }

        return Get("Cd") is { } cd ? new PanelResult(PanelResultKind.ChangeDirectory, cd) : null;
    }

    private void Refresh() =>
        RunInBackground(
            async ct =>
            {
                var result = await Pickle.Shell.InvokeAsync(ItemsScript, new Dictionary<string, object?> { ["__pickleScript"] = _spec.ItemsScript }, ShellTarget.Main, ct).ConfigureAwait(false);
                var items = result.Output
                    .Where(o => o is not null)
                    .Select((o, i) => new ListItem(o.Properties["I"]?.Value, o.Properties["D"]?.Value?.ToString() ?? string.Empty, i))
                    .ToList();
                return (items, result.Errors);
            },
            loaded =>
            {
                _list.SetItems(loaded.items);
                if (loaded.Errors.Count > 0)
                {
                    _preview.Show("Errors", loaded.Errors.Select(e => new PreviewLine(e.ToString(), Schemes.ErrorText.Foreground)));
                }
            },
            "loading…");

    private void RunAction(string label, ListItem item)
    {
        if (!_spec.Actions.TryGetValue(label, out var script))
        {
            return;
        }

        RunInBackground(
            ct => Pickle.Shell.InvokeAsync(ActionScript, new Dictionary<string, object?> { ["__pickleScript"] = script, ["__pickleItem"] = item.Value }, ShellTarget.Main, ct),
            result =>
            {
                foreach (var output in result.Output)
                {
                    if (output is not null && ToResult(output) is { } panelResult)
                    {
                        Complete(panelResult);
                        return;
                    }
                }

                var lines = result.Output.Where(o => o is not null).SelectMany(o => (o.ToString() ?? string.Empty).Split('\n')).Select(l => new PreviewLine(l.TrimEnd('\r')))
                    .Concat(result.Errors.Select(e => new PreviewLine(e.ToString(), Schemes.ErrorText.Foreground)))
                    .ToList();
                _previewVersion++;
                _preview.Show($"{label}: {item.Display}", lines.Count > 0 ? lines : [new PreviewLine("(done, no output)", Muted: true)]);
                Refresh();
            },
            label + "…");
    }

    private void UpdatePreview(ListItem? item)
    {
        var version = ++_previewVersion;
        if (item is null)
        {
            _preview.Clear();
            return;
        }

        var (script, parameters) = _spec.PreviewScript is { Length: > 0 } custom
            ? (CustomPreviewScript, new Dictionary<string, object?> { ["__pickleScript"] = custom, ["__pickleItem"] = item.Value })
            : (PreviewScript, new Dictionary<string, object?> { ["__pickleItem"] = item.Value });
        RunInBackground(
            ct => Pickle.Shell.InvokeAsync(script, parameters, ShellTarget.Main, ct),
            result =>
            {
                if (version != _previewVersion)
                {
                    return;
                }

                var text = string.Concat(result.Output.Select(o => o?.ToString())).Trim('\r', '\n');
                _preview.Show(item.Display, text.Split('\n').Select(l => l.TrimEnd('\r')));
            },
            "…");
    }

    internal sealed class ListItem(object? value, string display, int index)
    {
        public object? Value { get; } = value;

        public string Display { get; } = display;

        public int Index { get; } = index;

        public override string ToString() => Display;

        // Refreshing re-creates items; equal rows keep the selection (and the action output in the preview).
        public override bool Equals(object? obj) => obj is ListItem other && other.Index == Index && other.Display == Display;

        public override int GetHashCode() => HashCode.Combine(Index, Display);
    }
}
