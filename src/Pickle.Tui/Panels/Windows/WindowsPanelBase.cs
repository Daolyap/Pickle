using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui.Panels.Windows;

/// <summary>Shared plumbing for the winget, updates and scheduler panels (load-on-open, confirm/info hooks for tests).</summary>
public abstract class WindowsPanelBase : PanelWindow
{
    private bool _opened;

    protected WindowsPanelBase(PanelContext context, string title)
        : base(context, title)
    {
        IsRunningChanged += (_, e) =>
        {
            if (e.Value && !_opened)
            {
                _opened = true;
                Opened();
            }
        };
    }

    /// <summary>Tests replace the modal confirmation (title, message) → answer.</summary>
    internal Func<string, string, bool>? ConfirmHook { get; set; }

    /// <summary>Tests capture info/error messages instead of showing modal boxes.</summary>
    internal Action<string, string>? MessageHook { get; set; }

    /// <summary>Runs once the window is running (App is set, so background results reach the UI).</summary>
    protected abstract void Opened();

    /// <summary>Tests replace the multi-button question (title, message, buttons) → index, or -1 for cancel.</summary>
    internal Func<string, string, string[], int>? ChoiceHook { get; set; }

    /// <summary>Tests replace the text prompt (title, label) → text, or null for cancel.</summary>
    internal Func<string, string, string?>? PromptHook { get; set; }

    internal bool Ask(string title, string message) => ConfirmHook?.Invoke(title, message) ?? Confirm(title, message);

    /// <summary>Index of the chosen button; -1 when cancelled (Esc). Without a <see cref="ChoiceHook"/>, a
    /// <see cref="ConfirmHook"/> answers "yes" with the first button.</summary>
    internal int Choose(string title, string message, params string[] buttons)
    {
        if (ChoiceHook is { } choose)
        {
            return choose(title, message, buttons);
        }

        if (ConfirmHook is { } confirm)
        {
            return confirm(title, message) ? 0 : -1;
        }

        return App is { } app ? MessageBox.Query(app, title, message, buttons) ?? -1 : -1;
    }

    internal string? AskText(string title, string label, string initial = "") =>
        PromptHook is { } prompt ? prompt(title, label) : Prompt(title, label, initial);

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

    /// <summary><see cref="PanelWindow.RunInBackground{T}"/> for fire-and-forget work.</summary>
    internal void Run(Func<CancellationToken, Task> work, Action onDone, string busyText) =>
        RunInBackground(
            async ct =>
            {
                await work(ct).ConfigureAwait(false);
                return true;
            },
            _ => onDone(),
            busyText);

    internal void Load<T>(Func<CancellationToken, Task<T>> work, Action<T> onDone, string busyText) => RunInBackground(work, onDone, busyText);

    internal void Ui(Action action) => OnUi(action);

    internal T? Service<T>()
        where T : class => Pickle.Services.Get<T>();

    internal Scheme HighlightScheme()
    {
        var ui = Pickle.Themes.Current.Ui;
        return new Scheme(new Attribute(ToColor(ui.Success, new Color(143, 195, 74, 255)), ToColor(ui.PanelBackground, new Color(27, 31, 26, 255))))
        {
            Focus = new Attribute(ToColor(ui.HighlightForeground, new Color(255, 255, 255, 255)), ToColor(ui.HighlightBackground, new Color(58, 74, 50, 255))),
        };
    }

    internal static Color ToColor(string? value, Color fallback) =>
        PickleColor.Parse(value) is { IsRgb: true } c ? new Color(c.R, c.G, c.B, 255) : fallback;

    internal static int SelectedRow(TableView table) => table.Value?.SelectedCell.Y ?? -1;

    internal static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Accepting += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    internal static TextPane MakeText(string title) => new() { Title = title, Width = Dim.Fill(), Height = Dim.Fill() };

    internal static string When(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    internal static string Size(long? bytes) => bytes switch
    {
        null => string.Empty,
        >= 1024L * 1024 * 1024 => (bytes.Value / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes.Value / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes.Value / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
    };

    /// <summary>CommandLineToArgvW-compatible quoting (the task action runs <c>pickle -NoLogo -c "&lt;command&gt;"</c>).</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"' or '\n' or '\v'))
        {
            return argument;
        }

        var sb = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            sb.Append('\\', argument[i] == '"' ? (backslashes * 2) + 1 : backslashes).Append(argument[i]);
        }

        return sb.Append('"').ToString();
    }
}
