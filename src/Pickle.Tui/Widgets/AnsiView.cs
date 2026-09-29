using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace Pickle.Tui.Widgets;

/// <summary>One styled piece of an <see cref="AnsiView"/> line.</summary>
internal readonly record struct AnsiRun(string Text, PickleColor Foreground, PickleColor Background, bool Bold);

/// <summary>
/// Draws ANSI-colored lines (SGR only: 16 named, 256 and 24-bit colors, bold) in the colors of a given terminal palette,
/// with that palette's background behind them, e.g. a theme's sample prompt shown in the theme's own colors.
/// </summary>
internal sealed class AnsiView : View
{
    private IReadOnlyList<string> _lines = [];
    private TerminalPalette _palette = new();

    public AnsiView()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
    }

    public IReadOnlyList<string> Lines
    {
        get => _lines;
        set
        {
            _lines = value;
            SetNeedsDraw();
        }
    }

    /// <summary>Named colors, the default foreground and the background come from this palette.</summary>
    public TerminalPalette Palette
    {
        get => _palette;
        set
        {
            _palette = value;
            SetNeedsDraw();
        }
    }

    /// <summary>Splits an ANSI line into styled runs; escape sequences other than SGR are dropped.</summary>
    public static List<AnsiRun> Parse(string line, TerminalPalette palette)
    {
        var defaultFg = Rgb(palette.Foreground);
        var defaultBg = Rgb(palette.Background);
        var named = Named(palette);
        var (fg, bg, bold) = (defaultFg, defaultBg, false);
        var runs = new List<AnsiRun>();
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length > 0)
            {
                runs.Add(new AnsiRun(text.ToString(), fg, bg, bold));
                text.Clear();
            }
        }

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '\u001b')
            {
                text.Append(line[i]);
                continue;
            }

            // OSC (a title, a hyperlink): ESC ] … up to BEL or ESC \.
            if (i + 1 < line.Length && line[i + 1] == ']')
            {
                var bel = line.IndexOf('\u0007', i + 2);
                var st = line.IndexOf("\u001b\\", i + 2, StringComparison.Ordinal);
                i = (bel, st) switch
                {
                    ( < 0, < 0) => line.Length,
                    ( >= 0, < 0) => bel,
                    ( < 0, >= 0) => st + 1,
                    _ => Math.Min(bel, st + 1),
                };
                continue;
            }

            // CSI: ESC [ parameters final-byte; other escapes: skip the next character.
            if (i + 1 >= line.Length || line[i + 1] != '[')
            {
                i++;
                continue;
            }

            var end = i + 2;
            while (end < line.Length && line[end] is (>= '0' and <= '9') or ';' or ':' or '?')
            {
                end++;
            }

            if (end >= line.Length)
            {
                break;
            }

            if (line[end] == 'm')
            {
                Flush();
                var codes = line[(i + 2)..end].Split(';', ':').Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
                for (var c = 0; c < codes.Length; c++)
                {
                    switch (codes[c])
                    {
                        case 0:
                            (fg, bg, bold) = (defaultFg, defaultBg, false);
                            break;
                        case 1:
                            bold = true;
                            break;
                        case 22:
                            bold = false;
                            break;
                        case >= 30 and <= 37:
                            fg = named[codes[c] - 30];
                            break;
                        case >= 90 and <= 97:
                            fg = named[codes[c] - 90 + 8];
                            break;
                        case 39:
                            fg = defaultFg;
                            break;
                        case >= 40 and <= 47:
                            bg = named[codes[c] - 40];
                            break;
                        case >= 100 and <= 107:
                            bg = named[codes[c] - 100 + 8];
                            break;
                        case 49:
                            bg = defaultBg;
                            break;
                        case 38 or 48:
                            var foreground = codes[c] == 38;
                            if (Extended(codes, ref c, named) is { } color)
                            {
                                (fg, bg) = foreground ? (color, bg) : (fg, color);
                            }

                            break;
                    }
                }
            }

            i = end;
        }

        Flush();
        return runs;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var background = Attr(Rgb(_palette.Foreground), Rgb(_palette.Background), false);
        for (var y = 0; y < Viewport.Height; y++)
        {
            Move(0, y);
            SetAttribute(background);
            AddStr(new string(' ', Math.Max(0, Viewport.Width)));
            if (y >= _lines.Count)
            {
                continue;
            }

            Move(0, y);
            foreach (var run in Parse(_lines[y], _palette))
            {
                SetAttribute(Attr(run.Foreground, run.Background, run.Bold));
                AddStr(run.Text);
            }
        }

        return true;
    }

    // 38;2;r;g;b or 38;5;n (and 48 for backgrounds); advances c past the color's parameters.
    private static PickleColor? Extended(int[] codes, ref int c, PickleColor[] named)
    {
        if (c + 1 >= codes.Length)
        {
            return null;
        }

        if (codes[c + 1] == 2 && c + 4 < codes.Length)
        {
            var color = PickleColor.FromRgb((byte)Math.Clamp(codes[c + 2], 0, 255), (byte)Math.Clamp(codes[c + 3], 0, 255), (byte)Math.Clamp(codes[c + 4], 0, 255));
            c += 4;
            return color;
        }

        if (codes[c + 1] == 5 && c + 2 < codes.Length)
        {
            var n = Math.Clamp(codes[c + 2], 0, 255);
            c += 2;
            return n < 16 ? named[n] : Xterm256(n);
        }

        return null;
    }

    private static PickleColor Xterm256(int n)
    {
        if (n >= 232)
        {
            var level = (byte)(8 + ((n - 232) * 10));
            return PickleColor.FromRgb(level, level, level);
        }

        n -= 16;
        static byte Step(int v) => (byte)(v == 0 ? 0 : 55 + (v * 40));
        return PickleColor.FromRgb(Step(n / 36), Step(n / 6 % 6), Step(n % 6));
    }

    private static PickleColor Rgb(string value) => PickleColor.Parse(value) is { IsRgb: true } c ? c : PickleColor.FromRgb(0, 0, 0);

    private static PickleColor[] Named(TerminalPalette p) =>
    [
        Rgb(p.Black), Rgb(p.Red), Rgb(p.Green), Rgb(p.Yellow), Rgb(p.Blue), Rgb(p.Purple), Rgb(p.Cyan), Rgb(p.White),
        Rgb(p.BrightBlack), Rgb(p.BrightRed), Rgb(p.BrightGreen), Rgb(p.BrightYellow), Rgb(p.BrightBlue), Rgb(p.BrightPurple), Rgb(p.BrightCyan), Rgb(p.BrightWhite),
    ];

    private static Attribute Attr(PickleColor fg, PickleColor bg, bool bold) =>
        new(new Color(fg.R, fg.G, fg.B, 255), new Color(bg.R, bg.G, bg.B, 255), bold ? TextStyle.Bold : TextStyle.None);
}
