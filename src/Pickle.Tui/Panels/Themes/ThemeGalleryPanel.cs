using System.Text;
using Pickle.Abstractions;
using Pickle.Tui.Widgets;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Pickle.Tui.Panels.Themes;

/// <summary>
/// Every theme with a live preview in its own colors (palette, syntax, UI colors and a sample prompt, animated for
/// animated themes). Enter applies the selected theme; F2/F3 make it the light/dark theme of "auto".
/// </summary>
public sealed class ThemeGalleryPanel : PanelWindow
{
    public const string PanelId = "themes";

    private readonly FilterableList<Theme> _list;
    private readonly AnsiView _preview;
    private readonly long _openedAt = Environment.TickCount64;
    private long? _frame;

    public ThemeGalleryPanel(PanelContext context)
        : base(context, "Themes")
    {
        var current = Pickle.Themes.Current.Name;
        _list = new FilterableList<Theme>(t => t.Name)
        {
            Width = Dim.Percent(30),
            Hint = t => IsAnimated(t) ? "✦" : null,
            Detail = t => t.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? "current" : null,
            Keywords = t => (t.Description ?? string.Empty) + (IsAnimated(t) ? " animated" : string.Empty),
            Schemes = Schemes,
        };
        _preview = new AnsiView { X = Pos.Right(_list) + 1 };
        Body.Add(_list, _preview);

        _list.SelectionChanged += (_, _) => ShowSelected();
        _list.ItemAccepted += (_, theme) => Apply(theme);
        AddHint(Key.Enter, "Use it", () =>
        {
            if (_list.Selected is { } theme)
            {
                Apply(theme);
            }
        });
        AddHint(Key.F2, "Light theme for auto", () => SetAuto(light: true));
        AddHint(Key.F3, "Dark theme for auto", () => SetAuto(light: false));

        var themes = Pickle.Themes.Available.Select(Pickle.Themes.Load).OfType<Theme>().ToList();
        _list.SetItems(themes);
        if (themes.FirstOrDefault(t => t.Name.Equals(current, StringComparison.OrdinalIgnoreCase)) is { } selected)
        {
            _list.Select(selected);
        }

        ShowSelected();
        _list.Filter.SetFocus();
    }

    internal FilterableList<Theme> List => _list;

    internal AnsiView Preview => _preview;

    /// <summary>The preview of <paramref name="theme"/>: name, palette, syntax and UI colors, and a sample prompt.</summary>
    public static IReadOnlyList<string> PreviewLines(Theme theme, IThemePreviewer? previewer, int width, long? frame)
    {
        var lines = new List<string>
        {
            Ansi.Colorize(theme.Name, theme.Ui.Accent, bold: true) + (IsAnimated(theme) ? Ansi.Colorize("  ✦ animated", theme.Ui.Muted) : string.Empty),
            Ansi.Colorize(TextWidth.Truncate(theme.Description ?? string.Empty, Math.Max(0, width)), theme.Ui.Muted),
            string.Empty,
        };

        var t = theme.Terminal;
        lines.Add(Swatches(t.Black, t.Red, t.Green, t.Yellow, t.Blue, t.Purple, t.Cyan, t.White));
        lines.Add(Swatches(t.BrightBlack, t.BrightRed, t.BrightGreen, t.BrightYellow, t.BrightBlue, t.BrightPurple, t.BrightCyan, t.BrightWhite));
        lines.Add(string.Empty);

        var s = theme.Syntax;
        lines.Add(Ansi.Colorize("Get-ChildItem", s.Command) + Ansi.Colorize(" -Path ", s.Parameter) + Ansi.Colorize("'src'", s.String)
            + Ansi.Colorize(" | ", s.Operator) + Ansi.Colorize("Where-Object", s.Command) + " { " + Ansi.Colorize("$_", s.Variable)
            + Ansi.Colorize(".Length", s.Member) + Ansi.Colorize(" -gt ", s.Operator) + Ansi.Colorize("1kb", s.Number) + " }");
        lines.Add(Ansi.Colorize("# find big files", s.Comment) + "  " + Ansi.Colorize("Get-Nope", s.UnknownCommand));
        var u = theme.Ui;
        lines.Add(string.Join(' ', Ansi.Colorize("accent", u.Accent), Ansi.Colorize("success", u.Success), Ansi.Colorize("warning", u.Warning),
            Ansi.Colorize("error", u.Error), Ansi.Colorize("info", u.Info), Ansi.Colorize("muted", u.Muted)));
        lines.Add(string.Empty);

        if (previewer is not null)
        {
            lines.AddRange(previewer.Preview(theme, Math.Max(20, width), frame));
        }

        return lines;
    }

    /// <summary>Advances an animated preview to <paramref name="elapsedMs"/> after opening (the timer calls it; tests too).</summary>
    internal void Tick(long elapsedMs)
    {
        if (_list.Selected?.Prompt.Animation is not { } animation || !IsAnimated(_list.Selected))
        {
            return;
        }

        var frame = Math.Max(0, elapsedMs) / Math.Clamp(animation.FrameMs, 40, 2000);
        if (frame != _frame)
        {
            _frame = frame;
            ShowSelected();
        }
    }

    protected override void OnOpened() => Every(TimeSpan.FromMilliseconds(100), () => Tick(Environment.TickCount64 - _openedAt));

    private static bool IsAnimated(Theme theme) =>
        theme.Prompt.Animation is { } a && (!string.Equals(a.Effect, "none", StringComparison.OrdinalIgnoreCase) || a.PromptChars.Count > 1);

    private static string Swatches(params string[] colors)
    {
        var sb = new StringBuilder();
        foreach (var color in colors)
        {
            sb.Append(Ansi.Colorize("    ", null, color));
        }

        return sb.ToString();
    }

    private void ShowSelected()
    {
        if (_list.Selected is not { } theme)
        {
            _preview.Lines = [];
            return;
        }

        _preview.Palette = theme.Terminal;
        var width = _preview.Viewport.Width > 0 ? _preview.Viewport.Width : 60;
        _preview.Lines = PreviewLines(theme, Pickle.Services.Get<IThemePreviewer>(), width, IsAnimated(theme) ? _frame ?? 0 : null);
    }

    private void Apply(Theme theme)
    {
        if (IsClosed)
        {
            return;
        }

        Pickle.Themes.Apply(theme.Name);
        Close();
    }

    private void SetAuto(bool light)
    {
        if (_list.Selected is not { } theme)
        {
            return;
        }

        Pickle.Config.Update(c =>
        {
            if (light)
            {
                c.LightTheme = theme.Name;
            }
            else
            {
                c.DarkTheme = theme.Name;
            }
        });
        ShowInfo("Themes", $"{theme.Name} is now the {(light ? "light" : "dark")}-mode theme for auto (pk theme auto).");
    }
}
