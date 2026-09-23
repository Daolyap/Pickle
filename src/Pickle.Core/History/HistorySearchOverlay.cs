using System.Globalization;
using System.Text;
using Pickle.Abstractions;

namespace Pickle.Core.History;

public enum HistorySearchScope
{
    All,
    Directory,
    Session,
}

/// <summary>
/// Ctrl+R: fuzzy history search shown below the input. The overlay owns the query (typing edits it, not the buffer);
/// results are de-duplicated (most recent wins) and ranked by fuzzy score, then recency. Ctrl+R cycles the scope.
/// </summary>
public sealed class HistorySearchOverlay : IEditorOverlay
{
    private const int ResultLimit = 500;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IReadOnlyList<HistoryEntry> _entries;
    private readonly string _sessionId;
    private readonly string _cwd;
    private readonly string _originalText;
    private readonly int _originalCursor;
    private readonly UiColors _colors;
    private readonly int _maxRows;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<HistorySearchScope, List<HistoryEntry>> _candidates = [];
    private IReadOnlyList<(HistoryEntry Entry, FuzzyMatch Match)> _results = [];
    private int _selected;
    private int _scroll;

    public HistorySearchOverlay(
        IReadOnlyList<HistoryEntry> entries,
        string sessionId,
        string cwd,
        string originalText,
        int originalCursor,
        UiColors colors,
        int maxRows = 10,
        Func<DateTimeOffset>? clock = null)
    {
        _entries = entries;
        _sessionId = sessionId;
        _cwd = cwd;
        _originalText = originalText;
        _originalCursor = originalCursor;
        _colors = colors;
        _maxRows = Math.Max(1, maxRows);
        _clock = clock ?? (() => DateTimeOffset.Now);
        Query = originalText.Trim();
        Refilter();
    }

    public string Query { get; private set; }

    public HistorySearchScope Scope { get; private set; } = HistorySearchScope.All;

    public IReadOnlyList<(HistoryEntry Entry, FuzzyMatch Match)> Results => _results;

    public int SelectedIndex => _selected;

    public HistoryEntry? Selected => _selected >= 0 && _selected < _results.Count ? _results[_selected].Entry : null;

    public string? InputLineOverride
    {
        get
        {
            var scope = Scope switch
            {
                HistorySearchScope.Directory => "directory",
                HistorySearchScope.Session => "session",
                _ => "all",
            };
            var count = _results.Count >= ResultLimit ? $"{ResultLimit}+" : _results.Count.ToString(CultureInfo.InvariantCulture);
            return Ansi.Colorize("history", _colors.Accent, bold: true) + " " + Ansi.Colorize("❯", _colors.Accent) + " "
                + OneLine(Query) + Ansi.Colorize("▏", _colors.Accent) + "  "
                + Ansi.Colorize($"[{scope}]", _colors.Muted) + " " + Ansi.Colorize(count, _colors.Muted);
        }
    }

    internal static HistorySearchOverlay Create(PickleRuntime runtime, IHistoryStore store, IEditorBuffer buffer) =>
        new(
            store.Entries,
            store.SessionId,
            runtime.Engine.CurrentDirectory,
            buffer.Text,
            buffer.Cursor,
            runtime.Themes.Current.Ui,
            runtime.Config.Current.Editor.CompletionMenuMaxRows);

    public OverlayKeyResult HandleKey(ConsoleKeyInfo key, IEditorBuffer buffer)
    {
        var chord = KeyChord.FromKeyInfo(key);
        var ctrl = chord.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = chord.Modifiers.HasFlag(ConsoleModifiers.Alt);
        switch (chord.Key)
        {
            case ConsoleKey.R when ctrl && !alt:
                Scope = Scope switch
                {
                    HistorySearchScope.All => HistorySearchScope.Directory,
                    HistorySearchScope.Directory => HistorySearchScope.Session,
                    _ => HistorySearchScope.All,
                };
                Refilter();
                return OverlayKeyResult.Handled;
            case ConsoleKey.Escape:
            case ConsoleKey.C or ConsoleKey.G when ctrl && !alt:
                buffer.Replace(_originalText, _originalCursor);
                return OverlayKeyResult.Close;
            case ConsoleKey.Enter:
            case ConsoleKey.Tab:
            case ConsoleKey.RightArrow:
                if (Selected is { } entry)
                {
                    buffer.Replace(entry.CommandLine, entry.CommandLine.Length);
                }
                else
                {
                    buffer.Replace(_originalText, _originalCursor);
                }

                return OverlayKeyResult.Close;
            case ConsoleKey.UpArrow:
            case ConsoleKey.P when ctrl && !alt:
                Move(-1);
                return OverlayKeyResult.Handled;
            case ConsoleKey.DownArrow:
            case ConsoleKey.N when ctrl && !alt:
                Move(1);
                return OverlayKeyResult.Handled;
            case ConsoleKey.PageUp:
                Move(-_maxRows);
                return OverlayKeyResult.Handled;
            case ConsoleKey.PageDown:
                Move(_maxRows);
                return OverlayKeyResult.Handled;
            case ConsoleKey.Backspace when ctrl || alt:
            case ConsoleKey.W when ctrl && !alt:
                SetQuery(DeleteLastWord(Query));
                return OverlayKeyResult.Handled;
            case ConsoleKey.Backspace:
                SetQuery(Query.Length == 0 ? Query : Query[..PreviousTextElement(Query)]);
                return OverlayKeyResult.Handled;
            case ConsoleKey.U when ctrl && !alt:
                SetQuery(string.Empty);
                return OverlayKeyResult.Handled;
        }

        // AltGr arrives as Ctrl+Alt on Windows and still produces a printable character.
        var printable = key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && (ctrl == alt);
        if (printable)
        {
            SetQuery(Query + key.KeyChar);
        }

        return OverlayKeyResult.Handled;
    }

    public void OnBufferChanged(IEditorBuffer buffer)
    {
    }

    public IReadOnlyList<string> Render(int width, int maxRows)
    {
        var rows = Math.Min(_maxRows, maxRows);
        if (rows <= 0 || width <= 4)
        {
            return [];
        }

        var rowStyle = Ansi.Style(_colors.MenuForeground, _colors.MenuBackground);
        if (_results.Count == 0)
        {
            var text = TextWidth.PadRight("  no matching history", width);
            return [Ansi.Style(_colors.Muted, _colors.MenuBackground) + text + Ansi.Reset];
        }

        EnsureVisible(rows);
        var now = _clock();
        var lines = new List<string>(rows);
        for (var i = _scroll; i < Math.Min(_results.Count, _scroll + rows); i++)
        {
            lines.Add(RenderRow(_results[i].Entry, _results[i].Match, i == _selected, width, now, rowStyle));
        }

        return lines;
    }

    private string RenderRow(HistoryEntry entry, FuzzyMatch match, bool selected, int width, DateTimeOffset now, string normalStyle)
    {
        var style = selected ? Ansi.Style(_colors.MenuSelectedForeground, _colors.MenuSelectedBackground) : normalStyle;
        var metaStyle = selected ? style : Ansi.Style(_colors.MenuDescription, _colors.MenuBackground);
        var age = RelativeTime(now - entry.Timestamp);
        var dir = Scope == HistorySearchScope.Directory ? null : DirectoryHint(entry.Cwd);
        var meta = dir is null ? age : $"{dir}  {age}";

        // Layout: "❯ " + command + "  " + meta + " ". Drop the directory, then all metadata, when space is short.
        var commandWidth = width - 2 - 2 - TextWidth.VisibleWidth(meta) - 1;
        if (commandWidth < 12 && dir is not null)
        {
            meta = age;
            commandWidth = width - 2 - 2 - TextWidth.VisibleWidth(meta) - 1;
        }

        if (commandWidth < 12)
        {
            meta = string.Empty;
            commandWidth = width - 3;
        }

        var display = OneLine(entry.CommandLine);
        var shown = TextWidth.Truncate(display, commandWidth);
        var visibleChars = shown.Length == display.Length ? shown.Length : shown.Length - 1;
        var positions = match.Positions.Where(p => p < visibleChars).ToList();

        var sb = new StringBuilder(width * 2);
        sb.Append(style).Append(selected ? Ansi.Colorize("❯", null, bold: true) + style + " " : "  ");
        sb.Append(FuzzyMatcher.Highlight(shown, positions, selected ? null : _colors.MatchHighlight, style));
        sb.Append(' ', Math.Max(0, commandWidth - TextWidth.VisibleWidth(shown)));
        if (meta.Length > 0)
        {
            sb.Append("  ").Append(metaStyle).Append(meta).Append(style);
        }

        sb.Append(' ').Append(Ansi.Reset);
        return sb.ToString();
    }

    private void SetQuery(string query)
    {
        if (query == Query)
        {
            return;
        }

        Query = query;
        Refilter();
    }

    private void Refilter()
    {
        var selected = Selected;
        _results = FuzzyMatcher.Rank(Query, Candidates(Scope), e => e.CommandLine, ResultLimit);
        _selected = 0;
        _scroll = 0;
        if (selected is not null && string.IsNullOrEmpty(Query))
        {
            for (var i = 0; i < _results.Count; i++)
            {
                if (ReferenceEquals(_results[i].Entry, selected))
                {
                    _selected = i;
                    break;
                }
            }
        }
    }

    private List<HistoryEntry> Candidates(HistorySearchScope scope)
    {
        if (_candidates.TryGetValue(scope, out var cached))
        {
            return cached;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<HistoryEntry>();
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            var inScope = scope switch
            {
                HistorySearchScope.Directory => entry.Cwd is not null && string.Equals(TrimSeparators(entry.Cwd), TrimSeparators(_cwd), PathComparison),
                HistorySearchScope.Session => string.Equals(entry.SessionId, _sessionId, StringComparison.Ordinal),
                _ => true,
            };
            if (inScope && seen.Add(entry.CommandLine))
            {
                list.Add(entry);
            }
        }

        _candidates[scope] = list;
        return list;
    }

    private void Move(int delta)
    {
        if (_results.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _results.Count - 1);
    }

    private void EnsureVisible(int rows)
    {
        if (_selected < _scroll)
        {
            _scroll = _selected;
        }
        else if (_selected >= _scroll + rows)
        {
            _scroll = _selected - rows + 1;
        }

        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _results.Count - rows));
    }

    /// <summary>Single-line rendering that keeps char indices aligned with the original (for match positions).</summary>
    internal static string OneLine(string text)
    {
        if (text.AsSpan().IndexOfAny('\n', '\r', '\t') < 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] switch
                {
                    '\n' => '↵',
                    '\r' or '\t' => ' ',
                    var c => c,
                };
            }
        });
    }

    internal static string RelativeTime(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var inv = CultureInfo.InvariantCulture;
        return age.TotalSeconds < 60 ? "now"
            : age.TotalMinutes < 60 ? ((int)age.TotalMinutes).ToString(inv) + "m"
            : age.TotalHours < 24 ? ((int)age.TotalHours).ToString(inv) + "h"
            : age.TotalDays < 30 ? ((int)age.TotalDays).ToString(inv) + "d"
            : age.TotalDays < 365 ? ((int)(age.TotalDays / 30)).ToString(inv) + "mo"
            : ((int)(age.TotalDays / 365)).ToString(inv) + "y";
    }

    internal static string? DirectoryHint(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd))
        {
            return null;
        }

        var trimmed = TrimSeparators(cwd);
        var home = TrimSeparators(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (home.Length > 0 && string.Equals(trimmed, home, PathComparison))
        {
            return "~";
        }

        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    private static string TrimSeparators(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    private static int PreviousTextElement(string text)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length == 0 ? 0 : starts[^1];
    }

    private static string DeleteLastWord(string text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        while (end > 0 && !char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }
}
