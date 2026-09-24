using System.Collections.ObjectModel;
using Pickle.Abstractions;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Pickle.Tui.Panels.SystemMonitoring;

/// <summary>
/// Shared plumbing for the Processes, Network and Disks panels: Left/Right switch tabs, dialogs that tests can
/// intercept (<see cref="ConfirmHook"/>, <see cref="MessageHook"/>, <see cref="PickHook"/>) and a details dialog.
/// </summary>
public abstract class SystemPanelBase : PanelWindow
{
    private Tabs? _tabs;

    protected SystemPanelBase(PanelContext context, string title)
        : base(context, title)
    {
        // Reached when the focused view doesn't use the arrows (buttons, labels); tables are wired in TabKeys.
        KeyDown += (_, key) =>
        {
            if (_tabs is not null && (key == Key.CursorLeft || key == Key.CursorRight))
            {
                MoveTab(key == Key.CursorLeft ? -1 : 1);
                key.Handled = true;
            }
        };
    }

    /// <summary>Tests replace the modal confirmation (title, message) → answer.</summary>
    internal Func<string, string, bool>? ConfirmHook { get; set; }

    /// <summary>Tests capture info, error and details text (title, message) instead of modal dialogs.</summary>
    internal Action<string, string>? MessageHook { get; set; }

    /// <summary>Tests replace the pick list (title, options) → choice or null.</summary>
    internal Func<string, IReadOnlyList<string>, string?>? PickHook { get; set; }

    internal Tabs? TabView => _tabs;

    internal View? CurrentTab => _tabs?.Value;

    internal void MoveTab(int delta)
    {
        if (_tabs is null)
        {
            return;
        }

        var pages = _tabs.TabCollection.ToList();
        if (pages.Count == 0)
        {
            return;
        }

        var index = _tabs.Value is { } current ? pages.IndexOf(current) : 0;
        ShowTab(pages[(((index + delta) % pages.Count) + pages.Count) % pages.Count]);
    }

    internal void ShowTab(View page)
    {
        if (_tabs is not null)
        {
            _tabs.Value = page;
        }
    }

    internal bool Ask(string title, string message) => ConfirmHook?.Invoke(title, message) ?? Confirm(title, message);

    internal void Tell(string title, string message)
    {
        if (MessageHook is { } hook)
        {
            hook(title, message);
        }
        else
        {
            ShowInfo(title, message);
        }
    }

    internal void Fail(string message)
    {
        if (MessageHook is { } hook)
        {
            hook("Error", message);
        }
        else
        {
            ShowError(message);
        }
    }

    internal string? Choose(string title, IReadOnlyList<string> options) =>
        PickHook is { } hook ? hook(title, options) : Pick(title, options, o => o);

    /// <summary>A scrollable, read-only text dialog (long lines are wrapped).</summary>
    internal void ShowText(string title, IReadOnlyList<string> lines)
    {
        if (MessageHook is { } hook)
        {
            hook(title, string.Join('\n', lines));
            return;
        }

        if (App is not { } app)
        {
            return;
        }

        var width = Math.Max(40, (app.Screen.Width * 85 / 100) - 4);
        var wrapped = new ObservableCollection<string>(lines.SelectMany(line => Wrap(line, width)));
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(75) };
        var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true, ShowMarks = false };
        list.SetSource(wrapped);
        dialog.Add(list);
        dialog.AddButton(new Button { Title = "_OK" });
        dialog.SetScheme(Schemes.Dialog);
        list.SetScheme(Schemes.Dialog);
        app.Run(dialog);
    }

    internal static IEnumerable<string> Wrap(string line, int width)
    {
        if (line.Length <= width)
        {
            yield return line;
            yield break;
        }

        for (var i = 0; i < line.Length; i += width)
        {
            yield return line.Substring(i, Math.Min(width, line.Length - i));
        }
    }

    /// <summary>Create the panel's tabs (Left/Right move between them).</summary>
    protected Tabs CreateTabs(params View[] pages)
    {
        _tabs = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _tabs.Add(pages);
        _tabs.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } page)
            {
                OnTabChanged(page);
            }
        };
        return _tabs;
    }

    /// <summary>
    /// Left/Right on <paramref name="view"/> (a table, which would otherwise scroll sideways) switch tabs, unless
    /// <paramref name="handle"/> claims the key first.
    /// </summary>
    protected void TabKeys(View view, Func<Key, bool>? handle = null) =>
        view.KeyDown += (_, key) =>
        {
            if (key.Handled)
            {
                return;
            }

            if (handle?.Invoke(key) == true)
            {
                key.Handled = true;
            }
            else if (_tabs is not null && (key == Key.CursorLeft || key == Key.CursorRight))
            {
                MoveTab(key == Key.CursorLeft ? -1 : 1);
                key.Handled = true;
            }
        };

    /// <summary>Called when another tab is shown (e.g. to load it).</summary>
    protected virtual void OnTabChanged(View page)
    {
    }

    /// <summary>A status-bar entry that only describes keys handled elsewhere (e.g. "←→ Tabs").</summary>
    protected void AddNote(string text) => Hints.Add(new Shortcut { Title = text, CanFocus = false });
}
