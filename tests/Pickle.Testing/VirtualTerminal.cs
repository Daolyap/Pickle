using System.Globalization;
using System.Text;
using Pickle.Abstractions;
using Pickle.Core.Terminal;

namespace Pickle.Testing;

/// <summary>
/// An in-memory terminal for tests: scripted key input plus an ANSI/VT interpreter that maintains a cell grid.
/// Use <see cref="GetScreenText"/> / <see cref="GetStyledScreen"/> in snapshot tests to assert exactly what a user
/// would see. Supports cursor movement, erase, SGR styles, autowrap, scrolling, the alternate screen and titles.
/// </summary>
public sealed class VirtualTerminal : ITerminal
{
    private readonly Queue<(ConsoleKeyInfo Key, bool Burst)> _keys = new();
    private readonly StringBuilder _raw = new();
    private Cell[,] _cells;
    private Cell[,]? _savedMainScreen;
    private (int Row, int Col) _savedMainCursor;
    private (int Row, int Col) _savedCursor;
    private string _style = string.Empty;
    private bool _pendingWrap;

    public VirtualTerminal(int width = 80, int height = 24)
    {
        Width = width;
        Height = height;
        _cells = NewGrid(width, height);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool IsInteractive { get; set; } = true;

    public bool SupportsAnsi => true;

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public bool InAlternateScreen => _savedMainScreen is not null;

    public bool EditMode { get; private set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Lines scrolled off the top of the main screen.</summary>
    public List<string> Scrollback { get; } = [];

    /// <summary>Everything ever written, unparsed.</summary>
    public string RawOutput => _raw.ToString();

    /// <summary>When true (default), LF also returns to column 0 (like a tty with ONLCR).</summary>
    public bool NewlineIsCrLf { get; set; } = true;

    /// <summary>Called when the input queue is empty and a key is requested. Default throws <see cref="EndOfScriptedInputException"/>.</summary>
    public Func<ConsoleKeyInfo>? OnInputExhausted { get; set; }

    /// <summary>
    /// True only while the next queued key belongs to a <see cref="Paste"/> burst: keys scripted with
    /// <see cref="Type"/>/<see cref="Press"/> model a user typing one key at a time, so they are never "already available".
    /// </summary>
    public bool KeyAvailable => _keys.Count > 0 && _keys.Peek().Burst;

    /// <summary>Number of scripted keys not yet read.</summary>
    public int PendingKeyCount => _keys.Count;

    // ───────────── Input scripting ─────────────

    public VirtualTerminal Type(string text)
    {
        foreach (var c in text)
        {
            _keys.Enqueue((CharKey(c), false));
        }

        return this;
    }

    /// <summary>Enqueue text that arrives in one burst (like a terminal paste): <see cref="KeyAvailable"/> stays true within it.</summary>
    public VirtualTerminal Paste(string text)
    {
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            _keys.Enqueue((CharKey(c), true));
        }

        return this;
    }

    /// <summary>Enqueue chords like "Ctrl+R", "Enter", "Alt+G", "Shift+Tab", "RightArrow".</summary>
    public VirtualTerminal Press(params string[] chords)
    {
        foreach (var chord in chords)
        {
            var parsed = KeyChord.Parse(chord);
            var ch = parsed.Key switch
            {
                ConsoleKey.Enter => '\r',
                ConsoleKey.Tab => '\t',
                ConsoleKey.Escape => '\u001b',
                ConsoleKey.Backspace => '\b',
                ConsoleKey.Spacebar => ' ',
                >= ConsoleKey.A and <= ConsoleKey.Z when parsed.Modifiers.HasFlag(ConsoleModifiers.Control) => (char)(parsed.Key - ConsoleKey.A + 1),
                >= ConsoleKey.A and <= ConsoleKey.Z => (char)('a' + (parsed.Key - ConsoleKey.A)),
                0 => parsed.Char,
                _ => '\0',
            };
            var key = parsed.Key == 0 ? CharToKey(parsed.Char) : parsed.Key;
            _keys.Enqueue((new ConsoleKeyInfo(
                ch,
                key,
                parsed.Modifiers.HasFlag(ConsoleModifiers.Shift),
                parsed.Modifiers.HasFlag(ConsoleModifiers.Alt),
                parsed.Modifiers.HasFlag(ConsoleModifiers.Control)), false));
        }

        return this;
    }

    public VirtualTerminal Enqueue(ConsoleKeyInfo key)
    {
        _keys.Enqueue((key, false));
        return this;
    }

    public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_keys.Count > 0)
        {
            return _keys.Dequeue().Key;
        }

        return OnInputExhausted?.Invoke() ?? throw new EndOfScriptedInputException();
    }

    public void SetEditMode(bool editing) => EditMode = editing;

    /// <summary>Calls of <see cref="ITerminal.BeginNativeProgram"/> / <see cref="ITerminal.EndNativeProgram"/>, in order.</summary>
    public List<string> NativeProgramEvents { get; } = [];

    public void BeginNativeProgram() => NativeProgramEvents.Add("begin");

    public void EndNativeProgram() => NativeProgramEvents.Add("end");

    public (int Column, int Row) GetCursorPosition() => (CursorColumn, CursorRow);

    public void Flush()
    {
    }

    public void Resize(int width, int height)
    {
        var old = _cells;
        _cells = NewGrid(width, height);
        for (var r = 0; r < Math.Min(height, Height); r++)
        {
            for (var c = 0; c < Math.Min(width, Width); c++)
            {
                _cells[r, c] = old[r, c];
            }
        }

        Width = width;
        Height = height;
        CursorRow = Math.Min(CursorRow, height - 1);
        CursorColumn = Math.Min(CursorColumn, width - 1);
    }

    // ───────────── Screen inspection ─────────────

    /// <summary>Visible screen as text: rows joined by \n, trailing spaces and trailing blank rows removed.</summary>
    public string GetScreenText()
    {
        var lines = new List<string>(Height);
        for (var r = 0; r < Height; r++)
        {
            lines.Add(GetLine(r));
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    public string GetLine(int row)
    {
        var sb = new StringBuilder();
        for (var c = 0; c < Width; c++)
        {
            var cell = _cells[row, c];
            if (cell.Text is not null)
            {
                sb.Append(cell.Text);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Screen with style runs, one row per line: text runs are prefixed with «style» whenever the style changes
    /// (e.g. «fg=#B5E36B,bold»Get-ChildItem«»). Handy for snapshotting syntax highlighting.
    /// </summary>
    public string GetStyledScreen()
    {
        var rows = new List<string>();
        for (var r = 0; r < Height; r++)
        {
            var sb = new StringBuilder();
            var current = string.Empty;
            var lastNonBlank = -1;
            for (var c = 0; c < Width; c++)
            {
                if (_cells[r, c].Text is { } t && (t != " " || _cells[r, c].Style.Length > 0))
                {
                    lastNonBlank = c;
                }
            }

            for (var c = 0; c <= lastNonBlank; c++)
            {
                var cell = _cells[r, c];
                if (cell.Text is null)
                {
                    continue;
                }

                if (cell.Style != current)
                {
                    sb.Append('«').Append(cell.Style).Append('»');
                    current = cell.Style;
                }

                sb.Append(cell.Text);
            }

            if (current.Length > 0)
            {
                sb.Append("«»");
            }

            rows.Add(sb.ToString());
        }

        while (rows.Count > 0 && rows[^1].Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return string.Join('\n', rows);
    }

    /// <summary>Screen text with the cursor drawn as │ (useful when asserting cursor placement).</summary>
    public string GetScreenTextWithCursor()
    {
        var lines = new List<string>();
        for (var r = 0; r < Height; r++)
        {
            var sb = new StringBuilder();
            for (var c = 0; c < Width; c++)
            {
                if (r == CursorRow && c == CursorColumn)
                {
                    sb.Append('│');
                }

                if (_cells[r, c].Text is { } t)
                {
                    sb.Append(t);
                }
            }

            if (r == CursorRow && CursorColumn >= Width)
            {
                sb.Append('│');
            }

            lines.Add(sb.ToString().TrimEnd());
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    public void ClearRawOutput() => _raw.Clear();

    // ───────────── Output interpretation ─────────────

    public void Write(string text)
    {
        _raw.Append(text);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\u001b':
                    i = ParseEscape(text, i);
                    continue;
                case '\r':
                    CursorColumn = 0;
                    _pendingWrap = false;
                    break;
                case '\n':
                    LineFeed();
                    if (NewlineIsCrLf)
                    {
                        CursorColumn = 0;
                    }

                    break;
                case '\b':
                    CursorColumn = Math.Max(0, CursorColumn - 1);
                    _pendingWrap = false;
                    break;
                case '\t':
                    CursorColumn = Math.Min(Width - 1, ((CursorColumn / 8) + 1) * 8);
                    break;
                case '\a':
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length)
                    {
                        PutElement(text.Substring(i, 2));
                        i += 2;
                        continue;
                    }

                    if (c >= ' ')
                    {
                        PutElement(c.ToString());
                    }

                    break;
            }

            i++;
        }
    }

    private void PutElement(string element)
    {
        var w = TextWidth.ElementWidth(element);
        if (w == 0)
        {
            // Combining mark: append to previous cell.
            var col = Math.Max(0, CursorColumn - 1);
            _cells[CursorRow, col].Text += element;
            return;
        }

        if (_pendingWrap || CursorColumn + w > Width)
        {
            CursorColumn = 0;
            LineFeed();
            _pendingWrap = false;
        }

        _cells[CursorRow, CursorColumn] = new Cell(element, _style);
        if (w == 2 && CursorColumn + 1 < Width)
        {
            _cells[CursorRow, CursorColumn + 1] = new Cell(null, _style);
        }

        CursorColumn += w;
        if (CursorColumn >= Width)
        {
            CursorColumn = Width - 1;
            _pendingWrap = true;
        }
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (CursorRow == Height - 1)
        {
            ScrollUp(1);
        }
        else
        {
            CursorRow++;
        }
    }

    private void ScrollUp(int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (!InAlternateScreen)
            {
                Scrollback.Add(GetLine(0));
            }

            for (var r = 1; r < Height; r++)
            {
                for (var c = 0; c < Width; c++)
                {
                    _cells[r - 1, c] = _cells[r, c];
                }
            }

            ClearRow(Height - 1, 0, Width);
        }
    }

    private int ParseEscape(string text, int i)
    {
        if (i + 1 >= text.Length)
        {
            return text.Length;
        }

        var next = text[i + 1];
        if (next == '[')
        {
            var j = i + 2;
            var paramStart = j;
            while (j < text.Length && (text[j] < '@' || text[j] > '~'))
            {
                j++;
            }

            if (j >= text.Length)
            {
                return text.Length;
            }

            HandleCsi(text[paramStart..j], text[j]);
            return j + 1;
        }

        if (next == ']')
        {
            var j = i + 2;
            var start = j;
            while (j < text.Length && text[j] != '\u0007' && !(text[j] == '\u001b' && j + 1 < text.Length && text[j + 1] == '\\'))
            {
                j++;
            }

            var payload = text[start..Math.Min(j, text.Length)];
            if (payload.StartsWith("0;", StringComparison.Ordinal) || payload.StartsWith("2;", StringComparison.Ordinal))
            {
                Title = payload[2..];
            }

            return j >= text.Length ? text.Length : (text[j] == '\u0007' ? j + 1 : j + 2);
        }

        switch (next)
        {
            case '7':
                _savedCursor = (CursorRow, CursorColumn);
                break;
            case '8':
                (CursorRow, CursorColumn) = _savedCursor;
                break;
            case 'M':
                if (CursorRow > 0)
                {
                    CursorRow--;
                }

                break;
        }

        return i + 2;
    }

    private void HandleCsi(string parameters, char final)
    {
        var isPrivate = parameters.StartsWith('?');
        var p = (isPrivate ? parameters[1..] : parameters).Split(';');
        int Arg(int index, int fallback) =>
            index < p.Length && int.TryParse(p[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

        if (isPrivate)
        {
            var mode = Arg(0, 0);
            var on = final == 'h';
            switch (mode)
            {
                case 25:
                    CursorVisible = on;
                    break;
                case 1049:
                    if (on && _savedMainScreen is null)
                    {
                        _savedMainScreen = _cells;
                        _savedMainCursor = (CursorRow, CursorColumn);
                        _cells = NewGrid(Width, Height);
                        CursorRow = 0;
                        CursorColumn = 0;
                    }
                    else if (!on && _savedMainScreen is not null)
                    {
                        _cells = _savedMainScreen;
                        _savedMainScreen = null;
                        (CursorRow, CursorColumn) = _savedMainCursor;
                    }

                    break;
            }

            return;
        }

        _pendingWrap = false;
        switch (final)
        {
            case 'A':
                CursorRow = Math.Max(0, CursorRow - Arg(0, 1));
                break;
            case 'B':
                CursorRow = Math.Min(Height - 1, CursorRow + Arg(0, 1));
                break;
            case 'C':
                CursorColumn = Math.Min(Width - 1, CursorColumn + Arg(0, 1));
                break;
            case 'D':
                CursorColumn = Math.Max(0, CursorColumn - Arg(0, 1));
                break;
            case 'E':
                CursorRow = Math.Min(Height - 1, CursorRow + Arg(0, 1));
                CursorColumn = 0;
                break;
            case 'F':
                CursorRow = Math.Max(0, CursorRow - Arg(0, 1));
                CursorColumn = 0;
                break;
            case 'G':
                CursorColumn = Math.Clamp(Arg(0, 1) - 1, 0, Width - 1);
                break;
            case 'd':
                CursorRow = Math.Clamp(Arg(0, 1) - 1, 0, Height - 1);
                break;
            case 'H':
            case 'f':
                CursorRow = Math.Clamp(Arg(0, 1) - 1, 0, Height - 1);
                CursorColumn = Math.Clamp(Arg(1, 1) - 1, 0, Width - 1);
                break;
            case 'J':
                EraseDisplay(p.Length > 0 && int.TryParse(p[0], out var jm) ? jm : 0);
                break;
            case 'K':
                var km = p.Length > 0 && int.TryParse(p[0], out var k) ? k : 0;
                if (km == 0)
                {
                    ClearRow(CursorRow, CursorColumn, Width);
                }
                else if (km == 1)
                {
                    ClearRow(CursorRow, 0, CursorColumn + 1);
                }
                else
                {
                    ClearRow(CursorRow, 0, Width);
                }

                break;
            case 'X':
                ClearRow(CursorRow, CursorColumn, Math.Min(Width, CursorColumn + Arg(0, 1)));
                break;
            case 'P':
                DeleteChars(Arg(0, 1));
                break;
            case '@':
                InsertChars(Arg(0, 1));
                break;
            case 'S':
                ScrollUp(Arg(0, 1));
                break;
            case 's':
                _savedCursor = (CursorRow, CursorColumn);
                break;
            case 'u':
                (CursorRow, CursorColumn) = _savedCursor;
                break;
            case 'm':
                ApplySgr(p);
                break;
        }
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearRow(CursorRow, CursorColumn, Width);
                for (var r = CursorRow + 1; r < Height; r++)
                {
                    ClearRow(r, 0, Width);
                }

                break;
            case 1:
                for (var r = 0; r < CursorRow; r++)
                {
                    ClearRow(r, 0, Width);
                }

                ClearRow(CursorRow, 0, CursorColumn + 1);
                break;
            default:
                for (var r = 0; r < Height; r++)
                {
                    ClearRow(r, 0, Width);
                }

                if (mode == 3)
                {
                    Scrollback.Clear();
                }

                break;
        }
    }

    private void ClearRow(int row, int from, int to)
    {
        for (var c = Math.Max(0, from); c < Math.Min(Width, to); c++)
        {
            _cells[row, c] = Cell.Blank;
        }
    }

    private void DeleteChars(int n)
    {
        for (var c = CursorColumn; c < Width; c++)
        {
            _cells[CursorRow, c] = c + n < Width ? _cells[CursorRow, c + n] : Cell.Blank;
        }
    }

    private void InsertChars(int n)
    {
        for (var c = Width - 1; c >= CursorColumn; c--)
        {
            _cells[CursorRow, c] = c - n >= CursorColumn ? _cells[CursorRow, c - n] : Cell.Blank;
        }
    }

    private void ApplySgr(string[] p)
    {
        var attrs = ParseStyle(_style);
        if (p.Length == 0 || (p.Length == 1 && p[0].Length == 0))
        {
            _style = string.Empty;
            return;
        }

        for (var i = 0; i < p.Length; i++)
        {
            if (!int.TryParse(p[i], out var code))
            {
                continue;
            }

            switch (code)
            {
                case 0:
                    attrs.Clear();
                    break;
                case 1:
                    attrs["bold"] = "";
                    break;
                case 2:
                    attrs["dim"] = "";
                    break;
                case 3:
                    attrs["italic"] = "";
                    break;
                case 4:
                    attrs["underline"] = "";
                    break;
                case 7:
                    attrs["reverse"] = "";
                    break;
                case 22:
                    attrs.Remove("bold");
                    attrs.Remove("dim");
                    break;
                case 23:
                    attrs.Remove("italic");
                    break;
                case 24:
                    attrs.Remove("underline");
                    break;
                case 27:
                    attrs.Remove("reverse");
                    break;
                case >= 30 and <= 37:
                    attrs["fg"] = AnsiName(code - 30);
                    break;
                case >= 90 and <= 97:
                    attrs["fg"] = AnsiName(code - 90 + 8);
                    break;
                case 39:
                    attrs.Remove("fg");
                    break;
                case >= 40 and <= 47:
                    attrs["bg"] = AnsiName(code - 40);
                    break;
                case >= 100 and <= 107:
                    attrs["bg"] = AnsiName(code - 100 + 8);
                    break;
                case 49:
                    attrs.Remove("bg");
                    break;
                case 38:
                case 48:
                    var key = code == 38 ? "fg" : "bg";
                    if (i + 1 < p.Length && p[i + 1] == "2" && i + 4 < p.Length)
                    {
                        attrs[key] = string.Create(CultureInfo.InvariantCulture, $"#{int.Parse(p[i + 2], CultureInfo.InvariantCulture):X2}{int.Parse(p[i + 3], CultureInfo.InvariantCulture):X2}{int.Parse(p[i + 4], CultureInfo.InvariantCulture):X2}");
                        i += 4;
                    }
                    else if (i + 1 < p.Length && p[i + 1] == "5" && i + 2 < p.Length)
                    {
                        attrs[key] = "idx" + p[i + 2];
                        i += 2;
                    }

                    break;
            }
        }

        _style = FormatStyle(attrs);
    }

    private static Dictionary<string, string> ParseStyle(string style)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in style.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                d[part] = "";
            }
            else
            {
                d[part[..eq]] = part[(eq + 1)..];
            }
        }

        return d;
    }

    private static string FormatStyle(Dictionary<string, string> attrs)
    {
        var order = new[] { "fg", "bg", "bold", "dim", "italic", "underline", "reverse" };
        return string.Join(',', order.Where(attrs.ContainsKey).Select(k => attrs[k].Length == 0 ? k : $"{k}={attrs[k]}"));
    }

    private static string AnsiName(int index) => index switch
    {
        0 => "black",
        1 => "red",
        2 => "green",
        3 => "yellow",
        4 => "blue",
        5 => "purple",
        6 => "cyan",
        7 => "white",
        8 => "brightBlack",
        9 => "brightRed",
        10 => "brightGreen",
        11 => "brightYellow",
        12 => "brightBlue",
        13 => "brightPurple",
        14 => "brightCyan",
        _ => "brightWhite",
    };

    private static Cell[,] NewGrid(int width, int height)
    {
        var grid = new Cell[height, width];
        for (var r = 0; r < height; r++)
        {
            for (var c = 0; c < width; c++)
            {
                grid[r, c] = Cell.Blank;
            }
        }

        return grid;
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch) => new(ch, key, false, false, false);

    private static ConsoleKeyInfo CharKey(char c) => c switch
    {
        '\n' or '\r' => Key(ConsoleKey.Enter, '\r'),
        '\t' => Key(ConsoleKey.Tab, '\t'),
        ' ' => Key(ConsoleKey.Spacebar, ' '),
        _ => new ConsoleKeyInfo(c, CharToKey(c), shift: char.IsUpper(c), alt: false, control: false),
    };

    private static ConsoleKey CharToKey(char c) => c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
        >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
        ' ' => ConsoleKey.Spacebar,
        '-' => ConsoleKey.OemMinus,
        '+' or '=' => ConsoleKey.OemPlus,
        ',' or '<' => ConsoleKey.OemComma,
        '.' or '>' => ConsoleKey.OemPeriod,
        _ => 0,
    };

    private struct Cell(string? text, string style)
    {
        public static readonly Cell Blank = new(" ", string.Empty);

        /// <summary>Null for the trailing half of a wide character.</summary>
        public string? Text = text;

        public string Style = style;
    }
}

public sealed class EndOfScriptedInputException : Exception
{
    public EndOfScriptedInputException()
        : base("VirtualTerminal ran out of scripted keys. Enqueue more input (e.g. Press(\"Enter\")).")
    {
    }
}
