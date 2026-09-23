using System.Runtime.CompilerServices;
using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Pickle.Tui;

/// <summary>Terminal.Gui schemes and attributes derived from a Pickle theme (<see cref="Theme.Ui"/>).</summary>
public sealed class PanelSchemes
{
    /// <summary>Windows and plain views: panel colors, highlight on focus, accent hot keys.</summary>
    public required Scheme Base { get; init; }

    /// <summary>Lists, tables and menus: menu colors with the menu selection colors.</summary>
    public required Scheme List { get; init; }

    /// <summary>Dialogs and message boxes.</summary>
    public required Scheme Dialog { get; init; }

    /// <summary>Error dialogs.</summary>
    public required Scheme Error { get; init; }

    /// <summary>Text inputs (filter boxes, fields).</summary>
    public required Scheme Input { get; init; }

    /// <summary>Borders and separators.</summary>
    public required Scheme Border { get; init; }

    public required Attribute Normal { get; init; }
    public required Attribute Selected { get; init; }
    public required Attribute Muted { get; init; }
    public required Attribute MutedSelected { get; init; }
    public required Attribute Match { get; init; }
    public required Attribute MatchSelected { get; init; }
    public required Attribute Accent { get; init; }
    public required Attribute Success { get; init; }
    public required Attribute Warning { get; init; }
    public required Attribute ErrorText { get; init; }
    public required Attribute Info { get; init; }
}

/// <summary>
/// Maps the active Pickle theme (<see cref="Theme.Ui"/>) onto Terminal.Gui schemes. Everything is set per view
/// (never through the global SchemeManager) so concurrently running applications — tests — don't interfere.
/// Panels call <see cref="Apply(View, IPickleContext)"/>; <see cref="PanelHost"/> also styles every dialog.
/// </summary>
public static class PanelStyle
{
    private static readonly ConditionalWeakTable<Theme, PanelSchemes> Cache = new();
    private static readonly ConditionalWeakTable<View, Scheme> Styled = new();

    public static void Apply(View view, IPickleContext pickle) => Apply(view, pickle.Themes.Current);

    public static void Apply(View view, Theme theme)
    {
        var schemes = For(theme);
        view.SetScheme(view is Dialog ? schemes.Dialog : schemes.Base);
        StyleBorder(view, schemes);
        foreach (var sub in view.SubViews)
        {
            Restyle(sub, schemes);
        }

        view.SetNeedsDraw();
    }

    /// <summary>Style a dialog (message box, prompt) with the dialog scheme; errors get the error scheme.</summary>
    public static void ApplyDialog(View dialog, Theme theme, bool error = false)
    {
        var schemes = For(theme);
        dialog.SetScheme(error ? schemes.Error : schemes.Dialog);
        StyleBorder(dialog, schemes);
        dialog.SetNeedsDraw();
    }

    /// <summary>The schemes for a theme (cached per theme instance).</summary>
    public static PanelSchemes For(Theme theme) => Cache.GetValue(theme, Create);

    public static PanelSchemes For(IPickleContext pickle) => For(pickle.Themes.Current);

    /// <summary>
    /// Parse a theme color: "#RRGGBB", an ANSI name (resolved through the theme's terminal palette so the panel
    /// matches the terminal), or null/"default" → <paramref name="fallback"/>.
    /// </summary>
    public static Color ToColor(string? value, Theme theme, Color fallback)
    {
        if (PickleColor.Parse(value) is not { } parsed)
        {
            return fallback;
        }

        if (parsed.IsRgb)
        {
            return new Color(parsed.R, parsed.G, parsed.B);
        }

        var palette = theme.Terminal;
        var hex = parsed.AnsiIndex switch
        {
            0 => palette.Black,
            1 => palette.Red,
            2 => palette.Green,
            3 => palette.Yellow,
            4 => palette.Blue,
            5 => palette.Purple,
            6 => palette.Cyan,
            7 => palette.White,
            8 => palette.BrightBlack,
            9 => palette.BrightRed,
            10 => palette.BrightGreen,
            11 => palette.BrightYellow,
            12 => palette.BrightBlue,
            13 => palette.BrightPurple,
            14 => palette.BrightCyan,
            _ => palette.BrightWhite,
        };
        return PickleColor.Parse(hex) is { IsRgb: true } rgb ? new Color(rgb.R, rgb.G, rgb.B) : fallback;
    }

    private static void Restyle(View view, PanelSchemes schemes)
    {
        // Views a panel styled itself keep their scheme; the ones styled here follow theme changes.
        if (!view.HasScheme || Styled.TryGetValue(view, out _))
        {
            var scheme = view switch
            {
                ListView or TableView or TreeView => schemes.List,
                TextField or TextView => schemes.Input,
                Dialog => schemes.Dialog,
                _ => null,
            };
            if (scheme is not null)
            {
                view.SetScheme(scheme);
                Styled.AddOrUpdate(view, scheme);
            }
        }

        if (view is FrameView or Window)
        {
            StyleBorder(view, schemes);
        }

        foreach (var sub in view.SubViews)
        {
            Restyle(sub, schemes);
        }
    }

    private static void StyleBorder(View view, PanelSchemes schemes)
    {
        if (view.Border is { } border && border.Thickness != Thickness.Empty)
        {
            border.GetOrCreateView().SetScheme(schemes.Border);
        }
    }

    private static PanelSchemes Create(Theme theme)
    {
        var ui = theme.Ui;
        var defaults = new UiColors();
        Color C(string? value, string fallback) => ToColor(value, theme, ToColor(fallback, theme, Color.None));

        var panelBg = C(ui.PanelBackground, defaults.PanelBackground);
        var panelFg = C(ui.PanelForeground, defaults.PanelForeground);
        var border = C(ui.PanelBorder, defaults.PanelBorder);
        var highlightBg = C(ui.HighlightBackground, defaults.HighlightBackground);
        var highlightFg = C(ui.HighlightForeground, defaults.HighlightForeground);
        var menuBg = C(ui.MenuBackground, defaults.MenuBackground);
        var menuFg = C(ui.MenuForeground, defaults.MenuForeground);
        var selBg = C(ui.MenuSelectedBackground, defaults.MenuSelectedBackground);
        var selFg = C(ui.MenuSelectedForeground, defaults.MenuSelectedForeground);
        var description = C(ui.MenuDescription, defaults.MenuDescription);
        var accent = C(ui.Accent, defaults.Accent);
        var muted = C(ui.Muted, defaults.Muted);
        var match = C(ui.MatchHighlight, defaults.MatchHighlight);
        var error = C(ui.Error, defaults.Error);

        Attribute A(Color fg, Color bg, TextStyle style = TextStyle.None) => new(fg, bg, style);

        var baseScheme = new Scheme(A(panelFg, panelBg))
        {
            Focus = A(highlightFg, highlightBg),
            HotNormal = A(accent, panelBg, TextStyle.Bold),
            HotFocus = A(accent, highlightBg, TextStyle.Bold),
            Active = A(selFg, selBg),
            HotActive = A(selFg, selBg, TextStyle.Bold),
            Highlight = A(highlightFg, highlightBg),
            Editable = A(panelFg, menuBg),
            ReadOnly = A(muted, panelBg),
            Disabled = A(muted, panelBg),
        };

        var listScheme = new Scheme(A(menuFg, menuBg))
        {
            Focus = A(selFg, selBg),
            HotNormal = A(accent, menuBg, TextStyle.Bold),
            HotFocus = A(selFg, selBg, TextStyle.Bold),
            Active = A(highlightFg, highlightBg),
            HotActive = A(highlightFg, highlightBg, TextStyle.Bold),
            Highlight = A(highlightFg, highlightBg),
            Editable = A(menuFg, menuBg),
            ReadOnly = A(description, menuBg),
            Disabled = A(description, menuBg),
        };

        var dialogScheme = new Scheme(A(menuFg, menuBg))
        {
            Focus = A(selFg, selBg),
            HotNormal = A(accent, menuBg, TextStyle.Bold),
            HotFocus = A(selFg, selBg, TextStyle.Bold),
            Active = A(selFg, selBg),
            HotActive = A(selFg, selBg, TextStyle.Bold),
            Highlight = A(highlightFg, highlightBg),
            Editable = A(panelFg, panelBg),
            ReadOnly = A(description, menuBg),
            Disabled = A(description, menuBg),
        };

        var errorScheme = new Scheme(dialogScheme)
        {
            Normal = A(error, menuBg),
            HotNormal = A(error, menuBg, TextStyle.Bold),
        };

        var inputScheme = new Scheme(A(panelFg, menuBg))
        {
            Focus = A(highlightFg, menuBg),
            HotNormal = A(accent, menuBg),
            HotFocus = A(accent, menuBg, TextStyle.Bold),
            Active = A(selFg, selBg),
            Highlight = A(highlightFg, highlightBg),
            Editable = A(panelFg, menuBg),
            ReadOnly = A(muted, menuBg),
            Disabled = A(muted, menuBg),
        };

        var borderScheme = new Scheme(A(border, panelBg))
        {
            Focus = A(accent, panelBg),
            HotNormal = A(accent, panelBg, TextStyle.Bold),
            HotFocus = A(accent, panelBg, TextStyle.Bold),
            Active = A(accent, panelBg),
            Highlight = A(accent, panelBg),
            Disabled = A(muted, panelBg),
        };

        return new PanelSchemes
        {
            Base = baseScheme,
            List = listScheme,
            Dialog = dialogScheme,
            Error = errorScheme,
            Input = inputScheme,
            Border = borderScheme,
            Normal = A(menuFg, menuBg),
            Selected = A(selFg, selBg),
            Muted = A(description, menuBg),
            MutedSelected = A(selFg, selBg),
            Match = A(match, menuBg, TextStyle.Bold),
            MatchSelected = A(selFg, selBg, TextStyle.Bold | TextStyle.Underline),
            Accent = A(accent, panelBg, TextStyle.Bold),
            Success = A(C(ui.Success, defaults.Success), panelBg),
            Warning = A(C(ui.Warning, defaults.Warning), panelBg),
            ErrorText = A(error, panelBg),
            Info = A(C(ui.Info, defaults.Info), panelBg),
        };
    }
}
